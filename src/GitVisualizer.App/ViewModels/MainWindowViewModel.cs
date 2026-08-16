using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVisualizer.App.Services;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int DiffTabIndex = 0;
    private const int EditorTabIndex = 1;
    private const int DetailsTabIndex = 2;
    private const int ConflictTabIndex = 3;

    private static readonly HashSet<string> ExternalDocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".pdf", ".rtf", ".odt", ".ods", ".odp",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
        };

    private readonly IGitRepositoryService git;
    private readonly IDiffService diff;
    private readonly IRepositoryWatcherFactory watcherFactory;
    private readonly IFileWorkspaceService files;
    private readonly ISystemNewFileService systemNewFiles;
    private readonly ISettingsStore settingsStore;
    private readonly IOperationLogStore logStore;
    private readonly IRecoveryService recoveryService;
    private readonly ICredentialVault credentialVault;
    private IRepositoryWatcher? watcher;
    private CancellationTokenSource refreshCancellation = new();
    private readonly SemaphoreSlim editorSaveGate = new(1, 1);
    private AppSettings settings = AppSettings.Default;
    private int historyLoaded;
    private int repositorySortVersion;
    private int nextRepositoryOrder;
    private int fileTreeLoadVersion;
    private bool currentDocumentIsHistorical;
    private readonly Dictionary<string, int> repositoryInsertionOrder =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;

    public MainWindowViewModel(
        IGitRepositoryService git,
        IDiffService diff,
        IRepositoryWatcherFactory watcherFactory,
        IFileWorkspaceService files,
        ISystemNewFileService systemNewFiles,
        ISettingsStore settingsStore,
        IOperationLogStore logStore,
        IRecoveryService recoveryService,
        ICredentialVault credentialVault)
    {
        this.git = git;
        this.diff = diff;
        this.watcherFactory = watcherFactory;
        this.files = files;
        this.systemNewFiles = systemNewFiles;
        this.settingsStore = settingsStore;
        this.logStore = logStore;
        this.recoveryService = recoveryService;
        this.credentialVault = credentialVault;
    }

    public ObservableCollection<string> RecentRepositories { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ObservableCollection<TagInfo> Tags { get; } = [];
    public ObservableCollection<GitHistoryEvent> HistoryEvents { get; } = [];
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
    [ObservableProperty] private HeadInfo? head;
    [ObservableProperty] private BranchInfo? selectedBranch;
    [ObservableProperty] private RemoteInfo? selectedPushRemote;
    [ObservableProperty] private string selectedHistoryBranchName = string.Empty;
    [ObservableProperty] private string historyContextText = "全部分支";
    [ObservableProperty] private string statusText = "拖入文件夹，或点击“打开仓库”开始";
    [ObservableProperty] private string commitMessage = string.Empty;
    [ObservableProperty] private string diffText = string.Empty;
    [ObservableProperty] private string editorText = string.Empty;
    [ObservableProperty] private string detailsText = string.Empty;
    [ObservableProperty] private string equivalentCommand = string.Empty;
    [ObservableProperty] private int selectedRightTabIndex;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isCloning;
    [ObservableProperty] private string cloneDestinationPath = string.Empty;
    [ObservableProperty] private bool isPulling;
    [ObservableProperty] private string pullSourceText = "正在连接上游远程仓库";
    [ObservableProperty] private bool hasRepository;
    [ObservableProperty] private bool isExternalOnlyDocument;
    [ObservableProperty] private bool canSaveCurrentDocument;
    [ObservableProperty] private bool hasUnsavedEditorChanges;
    [ObservableProperty] private bool canOpenCurrentDocumentExternally;
    [ObservableProperty] private bool isBrowsingHistoricalCommit;
    [ObservableProperty] private bool canModifyFileTree = true;
    [ObservableProperty] private string fileTreeContextText = "工作区";
    [ObservableProperty] private string externalDocumentHint =
        "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
    [ObservableProperty] private TextDocument? currentDocument;
    [ObservableProperty] private FileChange? selectedChange;
    [ObservableProperty] private CommitNode? selectedCommit;
    [ObservableProperty] private OperationLogEntry? selectedOperationLog;
    [ObservableProperty] private ConflictFile? selectedConflict;
    [ObservableProperty] private RepositoryOperationState operationState;
    [ObservableProperty] private bool hasConflicts;
    [ObservableProperty] private bool hasSelectedConflict;
    [ObservableProperty] private bool canContinueOperation;
    [ObservableProperty] private bool canAbortOperation;
    [ObservableProperty] private string conflictStatusText = "当前没有进行中的冲突操作。";
    [ObservableProperty] private string conflictBaseText = string.Empty;
    [ObservableProperty] private string conflictOursText = string.Empty;
    [ObservableProperty] private string conflictTheirsText = string.Empty;
    [ObservableProperty] private string conflictResultText = string.Empty;

    public async Task InitializeAsync()
    {
        settings = await settingsStore.LoadAsync();
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
            await RememberRepositoryAsync(ActiveRepositoryPath);
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

    public async Task<bool> RemoveRecentRepositoryAsync(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        var existing = RecentRepositories.FirstOrDefault(
            item => Path.GetFullPath(item).Equals(
                normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        RecentRepositories.Remove(existing);
        repositoryInsertionOrder.Remove(existing);
        if (SelectedRepository is not null &&
            Path.GetFullPath(SelectedRepository).Equals(
                normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedRepository = null;
        }

        settings = settings with
        {
            RecentRepositories = RecentRepositories
                .OrderBy(item => repositoryInsertionOrder.GetValueOrDefault(item, int.MaxValue))
                .ToArray(),
            LastRepository = settings.LastRepository is not null &&
                             Path.GetFullPath(settings.LastRepository).Equals(
                                 normalizedPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : settings.LastRepository
        };
        await settingsStore.SaveAsync(settings);
        StatusText = $"已从仓库列表移除 {existing}；磁盘文件和 Git 数据未删除";
        return true;
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
        var normalizedPath = Path.GetFullPath(path);
        GitOperationResult? result = null;
        CloneDestinationPath = normalizedPath;
        IsCloning = true;
        try
        {
            await RunBusyAsync(async token =>
            {
                result = await git.CloneAsync(url, normalizedPath, credential, token);
                ShowResult(result);
            });
            if (result?.Success == true)
            {
                await OpenRepositoryAsync(normalizedPath);
            }
            return result ?? GitOperationResult.Fail(
                "clone",
                "git clone",
                new InvalidOperationException("当前有其他操作正在进行，未能开始克隆。"));
        }
        finally
        {
            IsCloning = false;
        }
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
        if (IsCurrentDocument(change.Path) &&
            !await SaveCurrentDocumentAsync(refreshAfterSave: false))
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
        if (!await SaveCurrentDocumentAsync(refreshAfterSave: false))
        {
            return;
        }

        RepositorySnapshot snapshot;
        try
        {
            snapshot = await git.GetSnapshotAsync(ActiveRepositoryPath);
        }
        catch (Exception exception)
        {
            StatusText = $"读取待暂存文件失败：{exception.Message}";
            return;
        }
        var paths = snapshot.Changes
            .Where(change => !change.IsStaged && change.State != GitChangeState.Ignored)
            .Select(change => change.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            StatusText = "没有可暂存的修改。";
            await RefreshAsync();
            return;
        }
        var result = await git.StageFilesAsync(
            ActiveRepositoryPath, paths);
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
    private async Task SaveEditorAsync() =>
        await SaveCurrentDocumentAsync(refreshAfterSave: true);

    [RelayCommand]
    private async Task SaveAndStageEditorAsync()
    {
        if (CurrentDocument is null || !CanSaveCurrentDocument)
        {
            return;
        }
        var documentPath = CurrentDocument.Path;
        if (!await SaveCurrentDocumentAsync(refreshAfterSave: false))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(ActiveRepositoryPath, documentPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            StatusText = "当前文件不在已打开的仓库中，不能暂存。";
            return;
        }

        var result = await git.StageFilesAsync(ActiveRepositoryPath, [relativePath]);
        ShowResult(result);
        await RefreshAsync();
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool refreshAfterSave)
    {
        if (CurrentDocument is null || !CanSaveCurrentDocument ||
            !HasUnsavedEditorChanges)
        {
            return true;
        }

        await editorSaveGate.WaitAsync();
        try
        {
            var document = CurrentDocument;
            if (document is null || !CanSaveCurrentDocument ||
                !HasUnsavedEditorChanges)
            {
                return true;
            }

            var textToSave = EditorText;
            await files.SaveTextAsync(document, textToSave, false);
            var savedDocument = await files.OpenTextAsync(document.Path);
            if (CurrentDocument is not null &&
                CurrentDocument.Path.Equals(document.Path, StringComparison.OrdinalIgnoreCase))
            {
                CurrentDocument = savedDocument;
                HasUnsavedEditorChanges =
                    !string.Equals(EditorText, savedDocument.Text, StringComparison.Ordinal);
            }
            StatusText = $"已保存 {Path.GetFileName(document.Path)}";
            if (refreshAfterSave)
            {
                await RefreshAsync();
            }
            return true;
        }
        catch (Exception exception)
        {
            StatusText = $"保存失败：{exception.Message}";
            return false;
        }
        finally
        {
            editorSaveGate.Release();
        }
    }

    [RelayCommand]
    private async Task OpenCurrentDocumentExternallyAsync()
    {
        if (CurrentDocument is not null && CanOpenCurrentDocumentExternally)
        {
            await OpenFileExternallyAsync(CurrentDocument.Path);
        }
        else if (CurrentDocument is not null)
        {
            StatusText = "历史版本文件为只读快照，不能直接交给外部程序打开。";
        }
    }

    public async Task<bool> OpenFileExternallyAsync(string path)
    {
        try
        {
            await files.OpenExternalAsync(path);
            StatusText = $"已使用系统默认程序打开 {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = $"无法使用系统默认程序打开：{exception.Message}";
            return false;
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
        var result = await git.FetchAsync(ActiveRepositoryPath, remote.Name, credential);
        await ReloadAllAsync();
        ShowResult(result);
    }

    public async Task<GitOperationResult> PullAsync(PullStrategy strategy)
    {
        var remote = Remotes.FirstOrDefault();
        if (!HasRepository || remote is null)
        {
            const string message = "当前仓库尚未配置可拉取的远程地址。";
            StatusText = message;
            return GitOperationResult.Fail(
                "pull",
                PullCommand(strategy),
                new InvalidOperationException(message));
        }
        if (IsBusy)
        {
            const string message = "当前有其他操作正在执行，请稍后再试。";
            StatusText = message;
            return GitOperationResult.Fail(
                "pull",
                PullCommand(strategy),
                new InvalidOperationException(message));
        }

        var overlayStarted = DateTime.UtcNow;
        PullSourceText = $"{remote.Name} → {Head?.BranchName ?? CurrentBranch}";
        IsBusy = true;
        IsPulling = true;
        try
        {
            GitOperationResult result;
            try
            {
                var credential = await GetRemoteCredentialAsync(remote);
                result = await git.PullAsync(
                    ActiveRepositoryPath,
                    strategy,
                    credential);
            }
            catch (Exception exception)
            {
                result = GitOperationResult.Fail(
                    "pull",
                    PullCommand(strategy),
                    exception);
            }

            var map = settings.PullStrategies.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            map[ActiveRepositoryPath] = strategy;
            settings = settings with { PullStrategies = map };
            await settingsStore.SaveAsync(settings);

            try
            {
                await ReloadAllAsync();
            }
            catch (Exception exception)
            {
                result = result with
                {
                    Warnings = result.Warnings
                        .Append($"拉取后刷新界面失败：{exception.Message}")
                        .ToArray()
                };
            }
            ShowResult(result);
            return result;
        }
        finally
        {
            var remaining = TimeSpan.FromMilliseconds(700) -
                            (DateTime.UtcNow - overlayStarted);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
            IsPulling = false;
            IsBusy = false;
        }
    }

    private static string PullCommand(PullStrategy strategy) => strategy switch
    {
        PullStrategy.Rebase => "git pull --rebase",
        PullStrategy.FastForwardOnly => "git pull --ff-only",
        _ => "git pull --no-rebase"
    };

    [RelayCommand]
    private async Task PushAsync()
    {
        await PushToRemoteAsync(SelectedPushRemote);
    }

    public async Task<GitOperationResult> PushToRemoteAsync(
        RemoteInfo? remote,
        IProgress<GitPushProgress>? progress = null)
    {
        if (remote is null)
        {
            StatusText = "仓库尚未配置远程地址。";
            return GitOperationResult.Fail(
                "push",
                "git push",
                new InvalidOperationException(StatusText));
        }
        if (IsBusy)
        {
            const string message = "当前有其他操作正在执行，请稍后再试。";
            StatusText = message;
            return GitOperationResult.Fail(
                "push",
                $"git push {remote.Name}",
                new InvalidOperationException(message));
        }

        IsBusy = true;
        try
        {
            progress?.Report(new GitPushProgress(
                GitPushProgressStage.Connecting,
                Message: $"正在准备凭据并连接 {remote.Name}"));
            GitOperationResult result;
            try
            {
                var credential = await GetRemoteCredentialAsync(remote);
                result = await git.PushAsync(
                    ActiveRepositoryPath,
                    remote.Name,
                    false,
                    credential,
                    progress);
            }
            catch (Exception exception)
            {
                result = GitOperationResult.Fail(
                    "push",
                    $"git push {remote.Name}",
                    exception);
            }

            try
            {
                await ReloadAllAsync();
            }
            catch (Exception exception)
            {
                result = result with
                {
                    Warnings = result.Warnings
                        .Append($"推送后刷新界面失败：{exception.Message}")
                        .ToArray()
                };
            }
            ShowResult(result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (!HasRepository)
        {
            return;
        }
        var commits = string.IsNullOrEmpty(SelectedHistoryBranchName)
            ? await git.GetHistoryAsync(ActiveRepositoryPath, historyLoaded, 200)
            : await git.GetBranchHistoryAsync(
                ActiveRepositoryPath,
                SelectedHistoryBranchName,
                historyLoaded,
                200);
        foreach (var commit in commits)
        {
            History.Add(commit);
        }
        historyLoaded += commits.Count;
        StatusText = commits.Count == 0
            ? "已经显示全部提交。"
            : string.IsNullOrEmpty(SelectedHistoryBranchName)
                ? $"已加载 {historyLoaded} 个提交 · 全部分支"
                : $"已加载 {historyLoaded} 个提交 · {SelectedHistoryBranchName} 分支";
    }

    public async Task SelectChangeAsync(FileChange? change)
    {
        SelectedChange = change;
        if (change is null)
        {
            DiffText = string.Empty;
            return;
        }
        SelectedRightTabIndex = DiffTabIndex;
        try
        {
            DiffText = await diff.GetUnifiedDiffAsync(
                ActiveRepositoryPath, change.Path, change.IsStaged);
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
        SelectedRightTabIndex = EditorTabIndex;
        if (item.CommitId is { } commitId)
        {
            await OpenCommitFileAsync(commitId, item.RelativePath);
        }
        else
        {
            await OpenFileAsync(item.FullPath);
        }
    }

    public async Task SelectCommitAsync(CommitNode? commit)
    {
        SelectedCommit = commit;
        if (commit is null)
        {
            await ShowWorkingTreeAsync();
            return;
        }

        SelectedRightTabIndex = DetailsTabIndex;
        var references = Branches
            .Where(branch =>
                string.Equals(branch.TipId, commit.Id, StringComparison.Ordinal))
            .Select(branch => branch.FriendlyName)
            .Concat(Tags
                .Where(tag =>
                    string.Equals(tag.TargetId, commit.Id, StringComparison.Ordinal))
                .Select(tag => $"tag:{tag.Name}"))
            .ToList();
        if (Head is not null &&
            string.Equals(Head.CommitId, commit.Id, StringComparison.Ordinal))
        {
            references.Insert(
                0,
                Head.IsDetached
                    ? "HEAD（游离）"
                    : $"HEAD -> {Head.BranchName}");
        }
        var relationshipExplanations = HistoryEvents
            .Where(historyEvent =>
                string.Equals(
                    historyEvent.CommitId,
                    commit.Id,
                    StringComparison.Ordinal))
            .Where(historyEvent => historyEvent.Kind is
                GitHistoryEventKind.CommitCreated or
                GitHistoryEventKind.BranchCreated or
                GitHistoryEventKind.BranchDeleted or
                GitHistoryEventKind.Checkout or
                GitHistoryEventKind.Reset or
                GitHistoryEventKind.Merge or
                GitHistoryEventKind.Revert)
            .Select(historyEvent => historyEvent.Description)
            .Distinct(StringComparer.CurrentCulture)
            .ToList();
        if (commit.ParentIds.Count > 1 &&
            !relationshipExplanations.Any(explanation =>
                explanation.Contains("merge commit", StringComparison.OrdinalIgnoreCase)))
        {
            relationshipExplanations.Add(
                $"该节点为 merge commit，包含 {commit.ParentIds.Count} 个父提交。");
        }
        if (relationshipExplanations.Count == 0)
        {
            relationshipExplanations.Add(
                "这是普通提交节点；连线仅表示 parent 关系，不表示提交归属于某个分支。");
        }
        DetailsText =
            $"{commit.ShortId}\n{commit.Message}\n\n作者：{commit.AuthorName} <{commit.AuthorEmail}>\n" +
            $"时间：{commit.AuthoredAt.LocalDateTime:G}\n" +
            $"父提交：{string.Join(", ", commit.ParentIds.Select(id => id[..Math.Min(8, id.Length)]))}\n" +
            $"引用：{string.Join(", ", references)}\n\n关系说明：\n" +
            string.Join("\n", relationshipExplanations.Select(explanation => $"• {explanation}"));

        var loadVersion = ++fileTreeLoadVersion;
        IsBrowsingHistoricalCommit = true;
        CanModifyFileTree = false;
        FileTreeContextText = $"版本 {commit.ShortId}";
        try
        {
            var entries = await git.GetCommitTreeAsync(ActiveRepositoryPath, commit.Id);
            if (loadVersion != fileTreeLoadVersion ||
                !string.Equals(SelectedCommit?.Id, commit.Id, StringComparison.Ordinal))
            {
                return;
            }

            BuildCommitFileTree(commit.Id, entries);
            StatusText = $"正在查看版本 {commit.ShortId} 的 {entries.Count(entry => !entry.IsDirectory)} 个文件";
        }
        catch (Exception exception)
        {
            if (loadVersion == fileTreeLoadVersion)
            {
                FileTree.Clear();
                StatusText = $"无法读取版本 {commit.ShortId} 的文件：{exception.Message}";
            }
        }
    }

    public async Task SelectBranchAsync(BranchInfo? branch)
    {
        if (branch is null || !HasRepository)
        {
            return;
        }

        SelectedBranch = branch;
        SelectedHistoryBranchName = branch.FriendlyName;
        HistoryContextText = $"{branch.FriendlyName} 分支版本关系";
        History.Clear();
        historyLoaded = 0;
        await LoadMoreHistoryAsync();

        var tip = History.FirstOrDefault(commit =>
            string.Equals(commit.Id, branch.TipId, StringComparison.Ordinal));
        if (tip is null)
        {
            StatusText = $"无法在已加载历史中找到分支 {branch.FriendlyName} 的最新版本";
            return;
        }

        await SelectCommitAsync(tip);
        FileTreeContextText = $"分支 {branch.FriendlyName} · {tip.ShortId}";
        StatusText = $"正在查看 {branch.FriendlyName} 分支的版本关系和最新文件";
    }

    [RelayCommand]
    private async Task ShowWorkingTreeAsync()
    {
        var restoreAllBranchHistory = !string.IsNullOrEmpty(SelectedHistoryBranchName);
        fileTreeLoadVersion++;
        SelectedCommit = null;
        SelectedBranch = null;
        SelectedHistoryBranchName = string.Empty;
        HistoryContextText = "全部分支";
        DetailsText = string.Empty;
        IsBrowsingHistoricalCommit = false;
        CanModifyFileTree = true;
        FileTreeContextText = "工作区";
        if (currentDocumentIsHistorical)
        {
            CurrentDocument = null;
            EditorText = string.Empty;
            HasUnsavedEditorChanges = false;
            IsExternalOnlyDocument = false;
            CanSaveCurrentDocument = false;
            CanOpenCurrentDocumentExternally = false;
            currentDocumentIsHistorical = false;
        }
        if (HasRepository)
        {
            BuildFileTree(ActiveRepositoryPath);
            StatusText = "正在显示当前工作区文件";
            if (restoreAllBranchHistory)
            {
                History.Clear();
                historyLoaded = 0;
                await LoadMoreHistoryAsync();
                StatusText = "正在显示当前工作区文件 · 全部分支关系";
            }
        }
    }

    public void SelectConflict(ConflictFile? conflict)
    {
        SelectedConflict = conflict;
        HasSelectedConflict = conflict is not null;
        if (conflict is not null)
        {
            SelectedRightTabIndex = ConflictTabIndex;
        }
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

    public Task<BranchDeletionCheck> CheckBranchDeletionAsync(BranchInfo branch) =>
        git.CheckBranchDeletionAsync(ActiveRepositoryPath, branch.FriendlyName);

    public async Task<GitOperationResult> DeleteBranchAsync(BranchInfo branch, bool force)
    {
        var result = await git.DeleteBranchAsync(
            ActiveRepositoryPath,
            branch.FriendlyName,
            force);
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

    public async Task SaveRemoteCredentialAsync(
        RemoteInfo remote,
        RemoteCredential credential)
    {
        var key = RemoteCredentialKey.Create(remote.FetchUrl);
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
            : $"已保存 {remote.Name} 的仓库专用凭据。";
    }

    public async Task SaveCloneCredentialAsync(
        string remoteUrl,
        RemoteCredential credential)
    {
        if (credential.Kind != CredentialKind.HttpsToken || !credential.Remember)
        {
            return;
        }
        await credentialVault.SaveAsync(
            RemoteCredentialKey.Create(remoteUrl),
            JsonSerializer.Serialize(credential));
        StatusText = "已将该仓库的 HTTPS 凭据保存到 Windows 凭据管理器。";
    }

    public async Task DeleteRemoteCredentialAsync(RemoteInfo remote)
    {
        await credentialVault.DeleteAsync(
            RemoteCredentialKey.Create(remote.FetchUrl));
        StatusText = $"已删除 {remote.Name} 的仓库专用凭据。";
    }

    public async Task<RemoteCredential?> LoadSavedRemoteCredentialAsync(RemoteInfo? remote = null)
    {
        remote ??= SelectedPushRemote ?? Remotes.FirstOrDefault();
        if (remote is null || IsSsh(remote.FetchUrl))
        {
            return null;
        }

        var json = await credentialVault.GetAsync(
            RemoteCredentialKey.Create(remote.FetchUrl));
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var credential = JsonSerializer.Deserialize<RemoteCredential>(json);
            return credential?.Kind == CredentialKind.HttpsToken
                ? credential
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<GitOperationResult> ConfigureRemoteAsync(
        string? originalName,
        string name,
        string url)
    {
        var result = originalName is null
            ? await git.AddRemoteAsync(ActiveRepositoryPath, name, url)
            : await git.UpdateRemoteAsync(
                ActiveRepositoryPath,
                originalName,
                name,
                url);
        ShowResult(result);
        await RefreshAsync();
        return result;
    }

    public async Task<GitOperationResult> RemoveRemoteAsync(string name)
    {
        var result = await git.RemoveRemoteAsync(ActiveRepositoryPath, name);
        ShowResult(result);
        await RefreshAsync();
        return result;
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

    public Task<IReadOnlyList<SystemNewFileType>> GetSystemNewFileTypesAsync() =>
        systemNewFiles.GetAvailableTypesAsync();

    public async Task CreateSystemFileAsync(
        string parentDirectory,
        string name,
        SystemNewFileType type)
    {
        await systemNewFiles.CreateAsync(Path.Combine(parentDirectory, name), type.Id);
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
        HasUnsavedEditorChanges =
            CurrentDocument is not null &&
            CanSaveCurrentDocument &&
            !string.Equals(value, CurrentDocument.Text, StringComparison.Ordinal);
    }

    private async Task OpenFileAsync(string path)
    {
        currentDocumentIsHistorical = false;
        HasUnsavedEditorChanges = false;
        try
        {
            CurrentDocument = await files.OpenTextAsync(path);
            IsExternalOnlyDocument =
                CurrentDocument.IsBinary ||
                ExternalDocumentExtensions.Contains(Path.GetExtension(path));
            CanSaveCurrentDocument = !CurrentDocument.IsReadOnly && !IsExternalOnlyDocument;
            CanOpenCurrentDocumentExternally = IsExternalOnlyDocument;
            ExternalDocumentHint =
                "DOCX、PDF、图片等文件不能在内置文本编辑器中直接编辑。请使用 Windows 默认程序打开。";
            EditorText = IsExternalOnlyDocument ? string.Empty : CurrentDocument.Text;
        }
        catch (Exception exception)
        {
            CurrentDocument = null;
            IsExternalOnlyDocument = false;
            CanSaveCurrentDocument = false;
            CanOpenCurrentDocumentExternally = false;
            EditorText = $"无法打开文件：{exception.Message}";
        }
    }

    private async Task OpenCommitFileAsync(string commitId, string relativePath)
    {
        currentDocumentIsHistorical = true;
        HasUnsavedEditorChanges = false;
        try
        {
            CurrentDocument = await git.OpenCommitFileAsync(
                ActiveRepositoryPath, commitId, relativePath);
            IsExternalOnlyDocument =
                CurrentDocument.IsBinary ||
                ExternalDocumentExtensions.Contains(Path.GetExtension(relativePath));
            CanSaveCurrentDocument = false;
            CanOpenCurrentDocumentExternally = false;
            ExternalDocumentHint =
                "这是历史提交中的只读文件。文本文件可直接查看；二进制或 Office 文件不会从当前工作区打开。";
            EditorText = IsExternalOnlyDocument ? string.Empty : CurrentDocument.Text;
        }
        catch (Exception exception)
        {
            CurrentDocument = null;
            IsExternalOnlyDocument = false;
            CanSaveCurrentDocument = false;
            CanOpenCurrentDocumentExternally = false;
            EditorText = $"无法打开历史文件：{exception.Message}";
        }
    }

    private async Task<RemoteCredential?> GetRemoteCredentialAsync(RemoteInfo? remote)
    {
        if (remote is null)
        {
            return null;
        }
        return await RemoteCredentialResolver.ResolveAsync(
            remote.FetchUrl,
            credentialVault);
    }

    private static bool IsSsh(string url) =>
        url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        (url.Contains('@', StringComparison.Ordinal) && url.Contains(':', StringComparison.Ordinal));

    private async Task ReloadAllAsync()
    {
        fileTreeLoadVersion++;
        SelectedCommit = null;
        SelectedBranch = null;
        SelectedHistoryBranchName = string.Empty;
        HistoryContextText = "全部分支";
        DetailsText = string.Empty;
        IsBrowsingHistoricalCommit = false;
        CanModifyFileTree = true;
        FileTreeContextText = "工作区";
        await RefreshAsync();
        History.Clear();
        historyLoaded = 0;
        await LoadMoreHistoryAsync();
    }

    private async Task ApplySnapshotAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var selectedRemoteName = SelectedPushRemote?.Name;
        Head = snapshot.Head;
        CurrentBranch = snapshot.Head.IsDetached
            ? $"游离 HEAD · {snapshot.Head.CommitId[..Math.Min(8, snapshot.Head.CommitId.Length)]}"
            : $"HEAD → {snapshot.Head.BranchName}";
        Replace(Branches, snapshot.Branches);
        Replace(Tags, snapshot.Tags);
        Replace(
            HistoryEvents,
            await git.GetHistoryEventsAsync(snapshot.RepositoryPath, cancellationToken));
        Replace(Remotes, snapshot.Remotes);
        SelectedPushRemote = Remotes.FirstOrDefault(remote =>
                                 remote.Name.Equals(
                                     selectedRemoteName,
                                     StringComparison.OrdinalIgnoreCase))
                             ?? Remotes.FirstOrDefault();
        Replace(UnstagedChanges, snapshot.Changes.Where(change => !change.IsStaged));
        Replace(StagedChanges, snapshot.Changes.Where(change => change.IsStaged));
        Replace(Notices, snapshot.Features.Notices);
        if (!IsBrowsingHistoricalCommit)
        {
            BuildFileTree(snapshot.WorkingDirectory);
        }
        Replace(OperationLog, await logStore.GetRecentAsync(snapshot.RepositoryPath, 100, cancellationToken));
        SelectedOperationLog = OperationLog.FirstOrDefault();
        var selectedConflictPath = SelectedConflict?.Path;
        Replace(Conflicts, await git.GetConflictsAsync(snapshot.RepositoryPath, cancellationToken));
        SelectConflict(
            Conflicts.FirstOrDefault(conflict =>
                conflict.Path.Equals(selectedConflictPath, StringComparison.OrdinalIgnoreCase))
            ?? Conflicts.FirstOrDefault());
        UpdateConflictState(snapshot.OperationState);
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

    private void BuildCommitFileTree(
        string commitId,
        IReadOnlyList<CommitTreeEntry> entries)
    {
        FileTree.Clear();
        var byParent = entries
            .Take(10000)
            .GroupBy(entry =>
            {
                var separator = entry.Path.LastIndexOf('/');
                return separator < 0 ? string.Empty : entry.Path[..separator];
            }, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        AddCommitTreeChildren(FileTree, string.Empty, commitId, byParent);
    }

    private void AddCommitTreeChildren(
        ObservableCollection<FileTreeItem> destination,
        string parentPath,
        string commitId,
        IReadOnlyDictionary<string, CommitTreeEntry[]> byParent)
    {
        if (!byParent.TryGetValue(parentPath, out var children))
        {
            return;
        }

        foreach (var entry in children
                     .OrderByDescending(item => item.IsDirectory)
                     .ThenBy(item => Path.GetFileName(item.Path), StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new FileTreeItem
            {
                Name = Path.GetFileName(entry.Path),
                FullPath = Path.Combine(ActiveRepositoryPath, entry.Path.Replace('/', Path.DirectorySeparatorChar)),
                RelativePath = entry.Path,
                CommitId = commitId,
                IsDirectory = entry.IsDirectory
            };
            destination.Add(item);
            if (entry.IsDirectory)
            {
                AddCommitTreeChildren(item.Children, entry.Path, commitId, byParent);
            }
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
        Head = null;
        StatusText = $"正在加载 {path}";
        historyLoaded = 0;
        SelectedBranch = null;
        SelectedHistoryBranchName = string.Empty;
        HistoryContextText = "全部分支";

        History.Clear();
        Branches.Clear();
        Tags.Clear();
        HistoryEvents.Clear();
        Remotes.Clear();
        SelectedPushRemote = null;
        UnstagedChanges.Clear();
        StagedChanges.Clear();
        FileTree.Clear();
        OperationLog.Clear();
        Conflicts.Clear();
        SelectConflict(null);
        UpdateConflictState(RepositoryOperationState.None);
        Notices.Clear();
        SelectedCommit = null;
        SelectedOperationLog = null;
        SelectedChange = null;
        CurrentDocument = null;
        currentDocumentIsHistorical = false;
        HasUnsavedEditorChanges = false;
        IsExternalOnlyDocument = false;
        CanSaveCurrentDocument = false;
        CanOpenCurrentDocumentExternally = false;
        IsBrowsingHistoricalCommit = false;
        CanModifyFileTree = true;
        FileTreeContextText = "工作区";
        fileTreeLoadVersion++;
        DiffText = string.Empty;
        EditorText = string.Empty;
        DetailsText = string.Empty;
        ConflictBaseText = string.Empty;
        ConflictOursText = string.Empty;
        ConflictTheirsText = string.Empty;
        ConflictResultText = string.Empty;
        EquivalentCommand = string.Empty;
    }

    private void UpdateConflictState(RepositoryOperationState state)
    {
        var previouslyHadConflicts = HasConflicts;
        OperationState = state;
        HasConflicts = Conflicts.Count > 0;
        HasSelectedConflict = SelectedConflict is not null;
        CanAbortOperation = state != RepositoryOperationState.None;
        CanContinueOperation = CanAbortOperation && !HasConflicts;
        ConflictStatusText = state switch
        {
            RepositoryOperationState.None when HasConflicts =>
                $"发现 {Conflicts.Count} 个冲突文件，请逐个处理。",
            RepositoryOperationState.None => "当前没有进行中的冲突操作。",
            _ when HasConflicts =>
                $"{OperationDisplayName(state)}进行中 · 剩余 {Conflicts.Count} 个冲突文件",
            _ => $"{OperationDisplayName(state)}的冲突已全部解决，可以继续操作。"
        };

        if (!previouslyHadConflicts && HasConflicts)
        {
            ConflictDetected?.Invoke(
                this,
                new ConflictDetectedEventArgs(
                    Conflicts.Count,
                    OperationDisplayName(state)));
        }
    }

    private static string OperationDisplayName(RepositoryOperationState state) => state switch
    {
        RepositoryOperationState.Merge => "合并",
        RepositoryOperationState.Rebase => "变基",
        RepositoryOperationState.CherryPick => "拣选提交",
        RepositoryOperationState.Revert => "撤销提交",
        RepositoryOperationState.Bisect => "二分查找",
        RepositoryOperationState.Unknown => "Git 操作",
        _ => "操作"
    };

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
        refreshCancellation.Cancel();
        refreshCancellation.Dispose();
    }

    private bool IsCurrentDocument(string relativePath)
    {
        if (CurrentDocument is null || !HasUnsavedEditorChanges)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(ActiveRepositoryPath, relativePath));
        return fullPath.Equals(
            Path.GetFullPath(CurrentDocument.Path),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RepositoryMetadata(
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        long Size)
    {
        public static RepositoryMetadata Empty { get; } = new(DateTime.MinValue, DateTime.MinValue, 0);
    }
}

public sealed record ConflictDetectedEventArgs(
    int ConflictCount,
    string OperationName);

public sealed class FileTreeItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string? CommitId { get; init; }
    public required bool IsDirectory { get; init; }
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public static FileTreeItem Create(string path, int depth)
    {
        var item = new FileTreeItem
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            RelativePath = path,
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
