using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IGitRepositoryService git;
    private readonly IDiffService diff;
    private readonly IIndexPatchService indexPatch;
    private readonly IRepositoryWatcherFactory watcherFactory;
    private readonly IFileWorkspaceService files;
    private readonly ISettingsStore settingsStore;
    private readonly IOperationLogStore logStore;
    private readonly IRecoveryService recoveryService;
    private readonly ICredentialVault credentialVault;
    private IRepositoryWatcher? watcher;
    private CancellationTokenSource refreshCancellation = new();
    private Timer? autoSaveTimer;
    private AppSettings settings = AppSettings.Default;
    private int historyLoaded;
    private int repositorySortVersion;
    private int nextRepositoryOrder;
    private readonly Dictionary<string, int> repositoryInsertionOrder =
        new(StringComparer.OrdinalIgnoreCase);

    public MainWindowViewModel(
        IGitRepositoryService git,
        IDiffService diff,
        IIndexPatchService indexPatch,
        IRepositoryWatcherFactory watcherFactory,
        IFileWorkspaceService files,
        ISettingsStore settingsStore,
        IOperationLogStore logStore,
        IRecoveryService recoveryService,
        ICredentialVault credentialVault)
    {
        this.git = git;
        this.diff = diff;
        this.indexPatch = indexPatch;
        this.watcherFactory = watcherFactory;
        this.files = files;
        this.settingsStore = settingsStore;
        this.logStore = logStore;
        this.recoveryService = recoveryService;
        this.credentialVault = credentialVault;
    }

    public ObservableCollection<string> RecentRepositories { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ObservableCollection<TagInfo> Tags { get; } = [];
    public ObservableCollection<RemoteInfo> Remotes { get; } = [];
    public ObservableCollection<FileChange> UnstagedChanges { get; } = [];
    public ObservableCollection<FileChange> StagedChanges { get; } = [];
    public ObservableCollection<CommitNode> History { get; } = [];
    public ObservableCollection<FileTreeItem> FileTree { get; } = [];
    public ObservableCollection<OperationLogEntry> OperationLog { get; } = [];
    public ObservableCollection<ConflictFile> Conflicts { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    public IReadOnlyList<string> RepositorySortModes { get; } =
        ["创建时间", "修改时间", "文件大小"];

    [ObservableProperty] private string activeRepositoryPath = string.Empty;
    [ObservableProperty] private string? selectedRepository;
    [ObservableProperty] private string repositorySortMode = "修改时间";
    [ObservableProperty] private string currentBranch = "未打开仓库";
    [ObservableProperty] private string statusText = "拖入文件夹，或点击“打开仓库”开始";
    [ObservableProperty] private string commitMessage = string.Empty;
    [ObservableProperty] private string diffText = string.Empty;
    [ObservableProperty] private string editorText = string.Empty;
    [ObservableProperty] private string detailsText = string.Empty;
    [ObservableProperty] private string equivalentCommand = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasRepository;
    [ObservableProperty] private bool autoSave;
    [ObservableProperty] private TextDocument? currentDocument;
    [ObservableProperty] private FileChange? selectedChange;
    [ObservableProperty] private CommitNode? selectedCommit;
    [ObservableProperty] private OperationLogEntry? selectedOperationLog;
    [ObservableProperty] private DiffHunk? selectedHunk;
    [ObservableProperty] private ConflictFile? selectedConflict;
    [ObservableProperty] private string conflictBaseText = string.Empty;
    [ObservableProperty] private string conflictOursText = string.Empty;
    [ObservableProperty] private string conflictTheirsText = string.Empty;
    [ObservableProperty] private string conflictResultText = string.Empty;

    public async Task InitializeAsync()
    {
        settings = await settingsStore.LoadAsync();
        AutoSave = settings.AutoSave;
        foreach (var repository in settings.RecentRepositories.Where(Directory.Exists))
        {
            RecentRepositories.Add(repository);
            repositoryInsertionOrder[repository] = nextRepositoryOrder++;
        }
        await SortRepositoriesAsync(RepositorySortMode);

        if (settings.LastRepository is { } last &&
            Directory.Exists(last) &&
            await git.IsRepositoryAsync(last))
        {
            await OpenRepositoryAsync(last);
        }
    }

    public Task<bool> IsRepositoryAsync(string path) =>
        git.IsRepositoryAsync(path);

    public async Task SortRepositoriesAsync(string mode)
    {
        if (!RepositorySortModes.Contains(mode, StringComparer.Ordinal))
        {
            return;
        }

        RepositorySortMode = mode;
        var version = ++repositorySortVersion;
        var paths = RecentRepositories.ToArray();
        var metadata = await Task.Run(() => paths.ToDictionary(
            path => path,
            path => ReadRepositoryMetadata(path, mode == "文件大小"),
            StringComparer.OrdinalIgnoreCase));
        if (version != repositorySortVersion)
        {
            return;
        }

        IOrderedEnumerable<string> ordered = mode switch
        {
            "创建时间" => paths.OrderByDescending(path => metadata[path].CreationTimeUtc),
            "修改时间" => paths.OrderByDescending(path => metadata[path].LastWriteTimeUtc),
            "文件大小" => paths.OrderByDescending(path => metadata[path].Size),
            _ => paths.OrderBy(path => repositoryInsertionOrder.GetValueOrDefault(path, int.MaxValue))
        };
        var sorted = ordered
            .ThenBy(path => repositoryInsertionOrder.GetValueOrDefault(path, int.MaxValue))
            .ToArray();
        for (var targetIndex = 0; targetIndex < sorted.Length; targetIndex++)
        {
            var currentIndex = RecentRepositories.IndexOf(sorted[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                RecentRepositories.Move(currentIndex, targetIndex);
            }
        }

        SelectedRepository = RecentRepositories.FirstOrDefault(
            path => path.Equals(ActiveRepositoryPath, StringComparison.OrdinalIgnoreCase));
        StatusText = $"仓库已按{mode}排序。";
    }

    public async Task<bool> OpenRepositoryAsync(string path)
    {
        var opened = false;
        var normalizedPath = Path.GetFullPath(path);
        if (HasRepository &&
            normalizedPath.Equals(ActiveRepositoryPath, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAsync();
            SelectedRepository = ActiveRepositoryPath;
            return true;
        }

        await RunBusyAsync(async token =>
        {
            ResetRepositoryView(normalizedPath);
            var snapshot = await git.GetSnapshotAsync(normalizedPath, token);
            ActiveRepositoryPath = snapshot.RepositoryPath;
            HasRepository = true;
            await RememberRepositoryAsync(snapshot.RepositoryPath);
            AttachWatcher(snapshot.RepositoryPath);
            await ApplySnapshotAsync(snapshot, token);
            opened = true;
        });
        return opened;
    }

    public async Task<GitOperationResult> InitializeRepositoryAsync(string path, GitIdentity identity)
    {
        var result = await git.InitializeAsync(path, identity);
        ShowResult(result);
        if (result.Success)
        {
            await OpenRepositoryAsync(path);
        }
        return result;
    }

    public async Task<GitOperationResult> CloneRepositoryAsync(
        string url, string path, RemoteCredential? credential)
    {
        GitOperationResult? result = null;
        await RunBusyAsync(async token =>
        {
            result = await git.CloneAsync(url, path, credential, token);
            ShowResult(result);
            if (result.Success)
            {
                await OpenRepositoryAsync(path);
            }
        });
        return result ?? GitOperationResult.Fail("clone", "git clone", new OperationCanceledException());
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!HasRepository)
        {
            return;
        }

        refreshCancellation.Cancel();
        refreshCancellation.Dispose();
        refreshCancellation = new CancellationTokenSource();
        try
        {
            var snapshot = await git.GetSnapshotAsync(ActiveRepositoryPath, refreshCancellation.Token);
            await ApplySnapshotAsync(snapshot, refreshCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer refresh replaced this one.
        }
        catch (Exception exception)
        {
            StatusText = $"刷新失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!HasRepository)
        {
            return;
        }
        var result = await git.CommitAsync(ActiveRepositoryPath, CommitMessage);
        ShowResult(result);
        if (result.Success)
        {
            CommitMessage = string.Empty;
            await ReloadAllAsync();
        }
    }

    [RelayCommand]
    private async Task AmendAsync()
    {
        if (!HasRepository)
        {
            return;
        }
        var result = await git.CommitAsync(ActiveRepositoryPath, CommitMessage, amend: true);
        ShowResult(result);
        await ReloadAllAsync();
    }

    [RelayCommand]
    private async Task StageAsync(FileChange? change)
    {
        if (change is null)
        {
            return;
        }
        var result = await git.StageFilesAsync(ActiveRepositoryPath, [change.Path]);
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UnstageAsync(FileChange? change)
    {
        if (change is null)
        {
            return;
        }
        var result = await git.UnstageFilesAsync(ActiveRepositoryPath, [change.Path]);
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (UnstagedChanges.Count == 0)
        {
            return;
        }
        var result = await git.StageFilesAsync(
            ActiveRepositoryPath, UnstagedChanges.Select(change => change.Path).ToArray());
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        if (StagedChanges.Count == 0)
        {
            return;
        }
        var result = await git.UnstageFilesAsync(
            ActiveRepositoryPath, StagedChanges.Select(change => change.Path).ToArray());
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task StageSelectedHunkAsync()
    {
        if (SelectedHunk is null || SelectedChange is null)
        {
            return;
        }
        var result = await indexPatch.StageHunksAsync(
            ActiveRepositoryPath, SelectedChange.Path, [SelectedHunk]);
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UnstageSelectedHunkAsync()
    {
        if (SelectedHunk is null || SelectedChange is null)
        {
            return;
        }
        var result = await indexPatch.UnstageHunksAsync(
            ActiveRepositoryPath, SelectedChange.Path, [SelectedHunk]);
        ShowResult(result);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (CurrentDocument is null)
        {
            return;
        }
        try
        {
            await files.SaveTextAsync(CurrentDocument, EditorText, false);
            CurrentDocument = await files.OpenTextAsync(CurrentDocument.Path);
            StatusText = $"已保存 {Path.GetFileName(CurrentDocument.Path)}";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"保存失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        var remote = Remotes.FirstOrDefault();
        if (remote is null)
        {
            StatusText = "仓库尚未配置远程地址。";
            return;
        }
        var credential = await GetRemoteCredentialAsync(remote);
        ShowResult(await git.FetchAsync(ActiveRepositoryPath, remote.Name, credential));
        await ReloadAllAsync();
    }

    public async Task PullAsync(PullStrategy strategy)
    {
        var credential = await GetRemoteCredentialAsync(Remotes.FirstOrDefault());
        ShowResult(await git.PullAsync(ActiveRepositoryPath, strategy, credential));
        var map = settings.PullStrategies.ToDictionary(pair => pair.Key, pair => pair.Value);
        map[ActiveRepositoryPath] = strategy;
        settings = settings with { PullStrategies = map };
        await settingsStore.SaveAsync(settings);
        await ReloadAllAsync();
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        var remote = Remotes.FirstOrDefault();
        if (remote is null)
        {
            StatusText = "仓库尚未配置远程地址。";
            return;
        }
        var credential = await GetRemoteCredentialAsync(remote);
        ShowResult(await git.PushAsync(ActiveRepositoryPath, remote.Name, false, credential));
        await ReloadAllAsync();
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (!HasRepository)
        {
            return;
        }
        var commits = await git.GetHistoryAsync(ActiveRepositoryPath, historyLoaded, 200);
        foreach (var commit in commits)
        {
            History.Add(commit);
        }
        historyLoaded += commits.Count;
        StatusText = commits.Count == 0 ? "已经显示全部提交。" : $"已加载 {historyLoaded} 个提交";
    }

    public async Task SelectChangeAsync(FileChange? change)
    {
        SelectedChange = change;
        SelectedHunk = null;
        if (change is null)
        {
            return;
        }
        try
        {
            DiffText = await diff.GetUnifiedDiffAsync(
                ActiveRepositoryPath, change.Path, change.IsStaged);
            var hunks = await diff.GetWorkingDiffAsync(
                ActiveRepositoryPath, change.Path, change.IsStaged);
            SelectedHunk = hunks.FirstOrDefault();
            await OpenFileAsync(Path.Combine(ActiveRepositoryPath, change.Path));
        }
        catch (Exception exception)
        {
            DiffText = $"无法显示差异：{exception.Message}";
        }
    }

    public async Task SelectFileAsync(FileTreeItem? item)
    {
        if (item is null || item.IsDirectory)
        {
            return;
        }
        await OpenFileAsync(item.FullPath);
    }

    public void SelectCommit(CommitNode? commit)
    {
        SelectedCommit = commit;
        DetailsText = commit is null
            ? string.Empty
            : $"{commit.ShortId}\n{commit.Message}\n\n作者：{commit.AuthorName} <{commit.AuthorEmail}>\n" +
              $"时间：{commit.AuthoredAt.LocalDateTime:G}\n父提交：{string.Join(", ", commit.ParentIds.Select(id => id[..8]))}\n" +
              $"引用：{string.Join(", ", commit.Decorations)}";
    }

    public void SelectConflict(ConflictFile? conflict)
    {
        SelectedConflict = conflict;
        ConflictBaseText = conflict?.BaseText ?? string.Empty;
        ConflictOursText = conflict?.OursText ?? string.Empty;
        ConflictTheirsText = conflict?.TheirsText ?? string.Empty;
        ConflictResultText = conflict?.ResultText ?? string.Empty;
    }

    public void UseConflictSide(ConflictSide side)
    {
        ConflictResultText = side switch
        {
            ConflictSide.Ours => ConflictOursText,
            ConflictSide.Theirs => ConflictTheirsText,
            ConflictSide.Both => ConflictOursText.TrimEnd() + Environment.NewLine +
                                 ConflictTheirsText.TrimStart(),
            _ => ConflictResultText
        };
    }

    public async Task<GitOperationResult> ResolveSelectedConflictAsync()
    {
        if (SelectedConflict is null)
        {
            throw new InvalidOperationException("请先选择冲突文件。");
        }
        var result = await git.ResolveConflictAsync(
            ActiveRepositoryPath, SelectedConflict.Path, ConflictResultText);
        ShowResult(result);
        await RefreshAsync();
        return result;
    }

    public async Task<GitOperationResult> CreateBranchAsync(string name)
    {
        var result = await git.CreateBranchAsync(ActiveRepositoryPath, name, SelectedCommit?.Id);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> CheckoutBranchAsync(BranchInfo branch)
    {
        var result = await git.CheckoutBranchAsync(ActiveRepositoryPath, branch.FriendlyName);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> MergeBranchAsync(BranchInfo branch)
    {
        var result = await git.MergeAsync(ActiveRepositoryPath, branch.FriendlyName);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> CherryPickSelectedAsync()
    {
        if (SelectedCommit is null)
        {
            throw new InvalidOperationException("请先选择一个提交。");
        }
        var result = await git.CherryPickAsync(ActiveRepositoryPath, SelectedCommit.Id);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> RevertSelectedAsync()
    {
        if (SelectedCommit is null)
        {
            throw new InvalidOperationException("请先选择一个提交。");
        }
        var result = await git.RevertAsync(ActiveRepositoryPath, SelectedCommit.Id);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> ResetSelectedAsync(GitResetMode mode)
    {
        if (SelectedCommit is null)
        {
            throw new InvalidOperationException("请先选择一个提交。");
        }
        var result = await git.ResetAsync(ActiveRepositoryPath, SelectedCommit.Id, mode);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> ContinueOperationAsync()
    {
        var result = await git.ContinueOperationAsync(ActiveRepositoryPath);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> AbortOperationAsync()
    {
        var result = await git.AbortOperationAsync(ActiveRepositoryPath);
        ShowResult(result);
        await ReloadAllAsync();
        return result;
    }

    public async Task<GitOperationResult> ConfigureIdentityAsync(
        GitIdentity identity, bool global)
    {
        var result = await git.SetIdentityAsync(ActiveRepositoryPath, identity, global);
        ShowResult(result);
        return result;
    }

    public async Task SaveRemoteCredentialAsync(RemoteCredential credential)
    {
        var remote = Remotes.FirstOrDefault()
                     ?? throw new InvalidOperationException("当前仓库没有远程地址。");
        var key = CredentialKey(remote.FetchUrl);
        if (credential.Kind == CredentialKind.SshAgent)
        {
            await credentialVault.DeleteAsync(key);
        }
        else
        {
            await credentialVault.SaveAsync(key, JsonSerializer.Serialize(credential));
        }
        StatusText = credential.Kind == CredentialKind.SshAgent
            ? "此远程将使用 Windows SSH Agent。"
            : "远程凭据已保存到 Windows 凭据管理器。";
    }

    public async Task CreateFileAsync(string parentDirectory, string name, bool directory)
    {
        var path = Path.Combine(parentDirectory, name);
        if (directory)
        {
            await files.CreateDirectoryAsync(path);
        }
        else
        {
            await files.CreateFileAsync(path);
        }
        await RefreshAsync();
    }

    public async Task MoveFileAsync(string source, string newName)
    {
        var destination = Path.Combine(
            Path.GetDirectoryName(source) ?? ActiveRepositoryPath, newName);
        await files.MoveAsync(source, destination);
        await RefreshAsync();
    }

    public async Task DeleteFileAsync(string path)
    {
        await files.DeleteAsync(path);
        await RefreshAsync();
    }

    public PullStrategy SavedPullStrategy =>
        settings.PullStrategies.TryGetValue(ActiveRepositoryPath, out var strategy)
            ? strategy
            : PullStrategy.Ask;

    public GitOperationPreview Preview(string operation, params string[] affected) =>
        git.Preview(operation, affected);

    partial void OnEditorTextChanged(string value)
    {
        if (!AutoSave || CurrentDocument is null || CurrentDocument.IsReadOnly)
        {
            return;
        }
        autoSaveTimer?.Dispose();
        autoSaveTimer = new Timer(async _ =>
        {
            await Application.Current.Dispatcher.InvokeAsync(async () => await SaveEditorAsync());
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    partial void OnAutoSaveChanged(bool value)
    {
        settings = settings with { AutoSave = value };
        _ = settingsStore.SaveAsync(settings);
    }

    private async Task OpenFileAsync(string path)
    {
        try
        {
            CurrentDocument = await files.OpenTextAsync(path);
            EditorText = CurrentDocument.IsBinary
                ? "二进制文件不能在内置编辑器中显示。"
                : CurrentDocument.Text;
        }
        catch (Exception exception)
        {
            CurrentDocument = null;
            EditorText = $"无法打开文件：{exception.Message}";
        }
    }

    private async Task<RemoteCredential?> GetRemoteCredentialAsync(RemoteInfo? remote)
    {
        if (remote is null)
        {
            return null;
        }
        if (IsSsh(remote.FetchUrl))
        {
            return new RemoteCredential(CredentialKind.SshAgent);
        }
        var json = await credentialVault.GetAsync(CredentialKey(remote.FetchUrl));
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RemoteCredential>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CredentialKey(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"remote:{uri.Scheme}:{uri.Host}";
        }
        var at = url.IndexOf('@');
        var colon = url.IndexOf(':', Math.Max(0, at));
        var host = at >= 0 && colon > at ? url[(at + 1)..colon] : url;
        return $"remote:ssh:{host}";
    }

    private static bool IsSsh(string url) =>
        url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        (url.Contains('@', StringComparison.Ordinal) && url.Contains(':', StringComparison.Ordinal));

    private async Task ReloadAllAsync()
    {
        await RefreshAsync();
        History.Clear();
        historyLoaded = 0;
        await LoadMoreHistoryAsync();
    }

    private async Task ApplySnapshotAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken)
    {
        CurrentBranch = snapshot.IsHeadDetached
            ? $"游离 HEAD · {snapshot.HeadId[..Math.Min(8, snapshot.HeadId.Length)]}"
            : snapshot.CurrentBranch;
        Replace(Branches, snapshot.Branches);
        Replace(Tags, snapshot.Tags);
        Replace(Remotes, snapshot.Remotes);
        Replace(UnstagedChanges, snapshot.Changes.Where(change => !change.IsStaged));
        Replace(StagedChanges, snapshot.Changes.Where(change => change.IsStaged));
        Replace(Notices, snapshot.Features.Notices);
        BuildFileTree(snapshot.WorkingDirectory);
        Replace(OperationLog, await logStore.GetRecentAsync(snapshot.RepositoryPath, 100, cancellationToken));
        SelectedOperationLog = OperationLog.FirstOrDefault();
        Replace(Conflicts, await git.GetConflictsAsync(snapshot.RepositoryPath, cancellationToken));
        StatusText = $"{snapshot.Changes.Count} 个变化 · {snapshot.Branches.Count} 个分支 · " +
                     $"刷新于 {snapshot.RefreshedAt:HH:mm:ss}";

        if (History.Count == 0)
        {
            historyLoaded = 0;
            await LoadMoreHistoryAsync();
        }
    }

    private void BuildFileTree(string root)
    {
        FileTree.Clear();
        try
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(root)
                         .Where(path => !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(Directory.Exists)
                         .ThenBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                         .Take(2000))
            {
                FileTree.Add(FileTreeItem.Create(item, 3));
            }
        }
        catch (IOException)
        {
            // Keep the tree partially populated if a path becomes unavailable.
        }
    }

    private void AttachWatcher(string path)
    {
        watcher?.Dispose();
        watcher = watcherFactory.Create(path);
        watcher.RepositoryChanged += async (_, _) =>
            await Application.Current.Dispatcher.InvokeAsync(async () => await RefreshAsync());
        watcher.Start();
    }

    private async Task RememberRepositoryAsync(string path)
    {
        var existing = RecentRepositories.FirstOrDefault(
            item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            RecentRepositories.Add(path);
            repositoryInsertionOrder[path] = nextRepositoryOrder++;
            existing = path;
        }
        while (RecentRepositories.Count > 20)
        {
            var oldest = RecentRepositories
                .Where(item => !item.Equals(existing, StringComparison.OrdinalIgnoreCase))
                .MinBy(item => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue));
            if (oldest is null)
            {
                break;
            }
            RecentRepositories.Remove(oldest);
            repositoryInsertionOrder.Remove(oldest);
        }
        settings = settings with
        {
            RecentRepositories = RecentRepositories
                .OrderBy(item => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue))
                .ToArray(),
            LastRepository = path
        };
        await settingsStore.SaveAsync(settings);
        await SortRepositoriesAsync(RepositorySortMode);
        SelectedRepository = existing;
    }

    private void ResetRepositoryView(string path)
    {
        watcher?.Dispose();
        watcher = null;
        refreshCancellation.Cancel();
        refreshCancellation.Dispose();
        refreshCancellation = new CancellationTokenSource();

        ActiveRepositoryPath = path;
        SelectedRepository = path;
        HasRepository = false;
        CurrentBranch = "正在打开仓库…";
        StatusText = $"正在加载 {path}";
        historyLoaded = 0;

        History.Clear();
        Branches.Clear();
        Tags.Clear();
        Remotes.Clear();
        UnstagedChanges.Clear();
        StagedChanges.Clear();
        FileTree.Clear();
        OperationLog.Clear();
        Conflicts.Clear();
        Notices.Clear();
        SelectedCommit = null;
        SelectedOperationLog = null;
        SelectedChange = null;
        SelectedHunk = null;
        SelectedConflict = null;
        CurrentDocument = null;
        DiffText = string.Empty;
        EditorText = string.Empty;
        DetailsText = string.Empty;
        ConflictBaseText = string.Empty;
        ConflictOursText = string.Empty;
        ConflictTheirsText = string.Empty;
        ConflictResultText = string.Empty;
        EquivalentCommand = string.Empty;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowResult(GitOperationResult result)
    {
        StatusText = result.Success
            ? result.Summary
            : $"{result.Summary}：{result.ErrorMessage}";
        EquivalentCommand = result.EquivalentCommand;
    }

    partial void OnSelectedOperationLogChanged(OperationLogEntry? value)
    {
        if (value is not null)
        {
            EquivalentCommand = value.EquivalentCommand;
        }
    }

    private static RepositoryMetadata ReadRepositoryMetadata(string path, bool includeSize)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return new RepositoryMetadata(
                directory.CreationTimeUtc,
                directory.LastWriteTimeUtc,
                includeSize ? CalculateDirectorySize(path) : 0);
        }
        catch (IOException)
        {
            return RepositoryMetadata.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return RepositoryMetadata.Empty;
        }
    }

    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            try
            {
                size = checked(size + new FileInfo(file).Length);
            }
            catch (IOException)
            {
                // A file may disappear while metadata is collected.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore files that cannot be inspected.
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }
        return size;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        autoSaveTimer?.Dispose();
        refreshCancellation.Cancel();
        refreshCancellation.Dispose();
    }

    private sealed record RepositoryMetadata(
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        long Size)
    {
        public static RepositoryMetadata Empty { get; } = new(DateTime.MinValue, DateTime.MinValue, 0);
    }
}

public sealed class FileTreeItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public static FileTreeItem Create(string path, int depth)
    {
        var item = new FileTreeItem
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = Directory.Exists(path)
        };
        if (item.IsDirectory && depth > 0)
        {
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)
                             .OrderByDescending(Directory.Exists)
                             .ThenBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                             .Take(500))
                {
                    item.Children.Add(Create(child, depth - 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The UI still displays the directory even if it cannot enumerate it.
            }
        }
        return item;
    }
}
