using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class RepositorySwitchingTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public async Task OpeningAnotherRepository_ReplacesHistoryAndSelection()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = Path.Combine(temporary.Path, "first");
        var secondPath = Path.Combine(temporary.Path, "second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        var settingsStore = new MemorySettingsStore();
        await CreateRepositoryAsync(git, firstPath, "第一个仓库提交");
        await CreateRepositoryAsync(git, secondPath, "第二个仓库提交");
        await File.WriteAllBytesAsync(Path.Combine(firstPath, "large.bin"), new byte[1024 * 1024]);

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            settingsStore,
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.Equal(
            ["创建时间", "修改时间", "文件大小"],
            viewModel.RepositorySortModes);
        Assert.DoesNotContain("添加顺序", viewModel.RepositorySortModes);

        Assert.True(await viewModel.OpenRepositoryAsync(firstPath));
        Assert.Equal("第一个仓库提交", Assert.Single(viewModel.History).Message);

        var textFile = Assert.Single(
            viewModel.FileTree,
            item => item.Name == "内容.txt");
        await viewModel.SelectFileAsync(textFile);
        Assert.Equal(1, viewModel.SelectedRightTabIndex);

        await viewModel.SelectCommitAsync(Assert.Single(viewModel.History));
        Assert.Equal(2, viewModel.SelectedRightTabIndex);

        await File.AppendAllTextAsync(Path.Combine(firstPath, "内容.txt"), "已修改");
        await viewModel.RefreshAsync();
        await viewModel.SelectChangeAsync(Assert.Single(
            viewModel.UnstagedChanges,
            change => change.Path == "内容.txt"));
        Assert.Equal(0, viewModel.SelectedRightTabIndex);

        Assert.True(await viewModel.OpenRepositoryAsync(secondPath));

        Assert.Equal(Path.GetFullPath(secondPath), viewModel.ActiveRepositoryPath);
        Assert.Equal(Path.GetFullPath(secondPath), viewModel.SelectedRepository);
        Assert.Equal("第二个仓库提交", Assert.Single(viewModel.History).Message);
        Assert.DoesNotContain(viewModel.History, commit => commit.Message == "第一个仓库提交");

        var originalOrder = viewModel.RecentRepositories.ToArray();
        Assert.True(await viewModel.OpenRepositoryAsync(firstPath));
        Assert.Equal(originalOrder, viewModel.RecentRepositories);
        Assert.Equal(Path.GetFullPath(firstPath), viewModel.SelectedRepository);

        await viewModel.SortRepositoriesAsync("文件大小");
        Assert.Equal(Path.GetFullPath(firstPath), viewModel.RecentRepositories[0]);
        Assert.Equal(Path.GetFullPath(firstPath), viewModel.SelectedRepository);

        var initLog = Assert.Single(
            viewModel.OperationLog, entry => entry.Operation == "init");
        viewModel.SelectedOperationLog = initLog;
        Assert.Equal("git init", viewModel.EquivalentCommand);

        Assert.True(await viewModel.RemoveRecentRepositoryAsync(firstPath));
        Assert.DoesNotContain(
            viewModel.RecentRepositories,
            path => path.Equals(firstPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            settingsStore.Settings.RecentRepositories,
            path => path.Equals(firstPath, StringComparison.OrdinalIgnoreCase));
        Assert.Null(settingsStore.Settings.LastRepository);
        Assert.True(viewModel.HasRepository);
        Assert.Equal(Path.GetFullPath(firstPath), viewModel.ActiveRepositoryPath);
        Assert.Null(viewModel.SelectedRepository);
        Assert.True(Directory.Exists(Path.Combine(firstPath, ".git")));
        Assert.True(File.Exists(Path.Combine(firstPath, "内容.txt")));

        Assert.True(await viewModel.OpenRepositoryAsync(firstPath));
        Assert.Contains(
            viewModel.RecentRepositories,
            path => path.Equals(firstPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectingHistoricalCommit_ReplacesFileTreeWithReadOnlySnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "history-tree");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        var initialized = await git.InitializeAsync(repositoryPath, Identity);
        Assert.True(initialized.Success, initialized.ErrorMessage);

        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "旧文件.txt"), "历史版本内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["旧文件.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "旧版本", Identity)).Success);

        File.Delete(Path.Combine(repositoryPath, "旧文件.txt"));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "新文件.txt"), "当前版本内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["旧文件.txt", "新文件.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "新版本", Identity)).Success);

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.Contains(viewModel.FileTree, item => item.Name == "新文件.txt");
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == "旧文件.txt");

        var oldCommit = Assert.Single(viewModel.History, commit => commit.Message == "旧版本");
        await viewModel.SelectCommitAsync(oldCommit);

        Assert.True(viewModel.IsBrowsingHistoricalCommit);
        Assert.False(viewModel.CanModifyFileTree);
        Assert.Equal($"版本 {oldCommit.ShortId}", viewModel.FileTreeContextText);
        var historicalFile = Assert.Single(viewModel.FileTree, item => item.Name == "旧文件.txt");
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == "新文件.txt");

        await viewModel.SelectFileAsync(historicalFile);
        Assert.Equal("历史版本内容", viewModel.EditorText);
        Assert.False(viewModel.CanSaveCurrentDocument);
        Assert.Contains($"@{oldCommit.ShortId}:", viewModel.CurrentDocument?.Path);

        viewModel.ShowWorkingTreeCommand.Execute(null);

        Assert.False(viewModel.IsBrowsingHistoricalCommit);
        Assert.True(viewModel.CanModifyFileTree);
        Assert.Equal("工作区", viewModel.FileTreeContextText);
        Assert.Contains(viewModel.FileTree, item => item.Name == "新文件.txt");
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == "旧文件.txt");
        Assert.Null(viewModel.CurrentDocument);
        Assert.Empty(viewModel.EditorText);
    }

    [Fact]
    public async Task SelectingBranch_FiltersGraphAndSynchronizesFileSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "branch-view");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        Assert.True((await git.InitializeAsync(repositoryPath, Identity)).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "共享.txt"), "base");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["共享.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "共同版本", Identity)).Success);

        Assert.True((await git.CreateBranchAsync(repositoryPath, "功能分支")).Success);
        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "功能分支")).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "功能文件.txt"), "feature");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["功能文件.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "功能分支版本", Identity)).Success);

        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "main")).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "主线文件.txt"), "main");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["主线文件.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "主线版本", Identity)).Success);

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.Contains(viewModel.History, commit => commit.Message == "功能分支版本");
        Assert.Contains(viewModel.History, commit => commit.Message == "主线版本");

        var feature = Assert.Single(
            viewModel.Branches,
            branch => branch.FriendlyName == "功能分支");
        await viewModel.SelectBranchAsync(feature);

        Assert.Equal("功能分支", viewModel.SelectedHistoryBranchName);
        Assert.Equal("功能分支 分支版本关系", viewModel.HistoryContextText);
        Assert.Contains(viewModel.History, commit => commit.Message == "功能分支版本");
        Assert.DoesNotContain(viewModel.History, commit => commit.Message == "主线版本");
        Assert.True(viewModel.IsBrowsingHistoricalCommit);
        Assert.False(viewModel.CanModifyFileTree);
        Assert.Contains("分支 功能分支", viewModel.FileTreeContextText);
        Assert.Contains(viewModel.FileTree, item => item.Name == "功能文件.txt");
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == "主线文件.txt");

        await viewModel.ShowWorkingTreeCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.SelectedHistoryBranchName);
        Assert.Equal("全部分支", viewModel.HistoryContextText);
        Assert.Contains(viewModel.History, commit => commit.Message == "功能分支版本");
        Assert.Contains(viewModel.History, commit => commit.Message == "主线版本");
        Assert.Contains(viewModel.FileTree, item => item.Name == "主线文件.txt");
        Assert.DoesNotContain(viewModel.FileTree, item => item.Name == "功能文件.txt");
    }

    [Fact]
    public async Task StageAll_SavesPendingEditorTextBeforeStaging()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "editor-stage");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "原始内容");

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        var file = Assert.Single(viewModel.FileTree, item => item.Name == "内容.txt");
        await viewModel.SelectFileAsync(file);
        viewModel.EditorText = "编辑器中的新内容";

        Assert.True(viewModel.HasUnsavedEditorChanges);
        await viewModel.StageAllCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasUnsavedEditorChanges);
        Assert.Equal(
            "编辑器中的新内容",
            await File.ReadAllTextAsync(Path.Combine(repositoryPath, "内容.txt")));
        var change = Assert.Single(
            (await git.GetSnapshotAsync(repositoryPath)).Changes,
            item => item.Path == "内容.txt");
        Assert.True(change.IsStaged);
    }

    [Fact]
    public async Task ConflictWorkspace_SelectsFirstFileAndGuidesTheOperationState()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "conflict-workspace");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "初始版本");
        Assert.True((await git.CreateBranchAsync(repositoryPath, "feature-conflict")).Success);

        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "来自 main");
        await git.StageFilesAsync(repositoryPath, ["内容.txt"]);
        await git.CommitAsync(repositoryPath, "main 修改", Identity);

        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "feature-conflict")).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "来自 feature");
        await git.StageFilesAsync(repositoryPath, ["内容.txt"]);
        await git.CommitAsync(repositoryPath, "feature 修改", Identity);
        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "main")).Success);

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        var conflictNotifications = new List<ConflictDetectedEventArgs>();
        viewModel.ConflictDetected += (_, notification) =>
            conflictNotifications.Add(notification);
        var feature = Assert.Single(
            viewModel.Branches,
            branch => branch.FriendlyName == "feature-conflict");
        var merged = await viewModel.MergeBranchAsync(feature);

        Assert.True(merged.Success, merged.ErrorMessage);
        Assert.True(viewModel.HasConflicts);
        Assert.True(viewModel.HasSelectedConflict);
        Assert.NotNull(viewModel.SelectedConflict);
        Assert.Contains("剩余 1 个冲突文件", viewModel.ConflictStatusText);
        Assert.False(viewModel.CanContinueOperation);
        Assert.True(viewModel.CanAbortOperation);
        var notification = Assert.Single(conflictNotifications);
        Assert.Equal(1, notification.ConflictCount);
        Assert.Equal("合并", notification.OperationName);

        await viewModel.RefreshAsync();
        Assert.Single(conflictNotifications);

        viewModel.UseConflictSide(ConflictSide.Both);
        Assert.Contains("来自 main", viewModel.ConflictResultText);
        Assert.Contains("来自 feature", viewModel.ConflictResultText);
        var resolved = await viewModel.ResolveSelectedConflictAsync();

        Assert.True(resolved.Success, resolved.ErrorMessage);
        Assert.False(viewModel.HasConflicts);
        Assert.False(viewModel.HasSelectedConflict);
        Assert.True(viewModel.CanContinueOperation);
        Assert.Contains("可以继续操作", viewModel.ConflictStatusText);
    }

    [Fact]
    public async Task CloneRepository_ShowsCloneStateAndOpensDestination()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "clone-source");
        var destinationPath = Path.Combine(temporary.Path, "clone-destination");
        Directory.CreateDirectory(sourcePath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, sourcePath, "可克隆版本");

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());
        var cloneStates = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsCloning))
            {
                cloneStates.Add(viewModel.IsCloning);
            }
        };

        var result = await viewModel.CloneRepositoryAsync(
            sourcePath,
            destinationPath,
            null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([true, false], cloneStates);
        Assert.False(viewModel.IsCloning);
        Assert.Equal(Path.GetFullPath(destinationPath), viewModel.CloneDestinationPath);
        Assert.True(viewModel.HasRepository);
        Assert.Equal(Path.GetFullPath(destinationPath), viewModel.ActiveRepositoryPath);
        Assert.Equal("可克隆版本", Assert.Single(viewModel.History).Message);
    }

    [Fact]
    public async Task PullRepository_ShowsPullStateAndLoadsRemoteCommit()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "pull-source");
        var clonePath = Path.Combine(temporary.Path, "pull-clone");
        Directory.CreateDirectory(sourcePath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, sourcePath, "初始版本");
        var cloned = await git.CloneAsync(sourcePath, clonePath);
        Assert.True(cloned.Success, cloned.ErrorMessage);

        await File.WriteAllTextAsync(Path.Combine(sourcePath, "远程更新.txt"), "来自远程的新内容");
        Assert.True((await git.StageFilesAsync(sourcePath, ["远程更新.txt"])).Success);
        Assert.True((await git.CommitAsync(sourcePath, "远程更新", Identity)).Success);

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());
        Assert.True(await viewModel.OpenRepositoryAsync(clonePath));

        var pullStates = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsPulling))
            {
                pullStates.Add(viewModel.IsPulling);
            }
        };

        var result = await viewModel.PullAsync(PullStrategy.Merge);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([true, false], pullStates);
        Assert.False(viewModel.IsPulling);
        Assert.Contains("origin", viewModel.PullSourceText);
        Assert.Contains(viewModel.History, commit => commit.Message == "远程更新");
        Assert.True(File.Exists(Path.Combine(clonePath, "远程更新.txt")));
    }

    private static async Task CreateRepositoryAsync(
        IGitRepositoryService git,
        string path,
        string message)
    {
        var initialized = await git.InitializeAsync(path, Identity);
        Assert.True(initialized.Success, initialized.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(path, "内容.txt"), message);
        var staged = await git.StageFilesAsync(path, ["内容.txt"]);
        Assert.True(staged.Success, staged.ErrorMessage);
        var committed = await git.CommitAsync(path, message, Identity);
        Assert.True(committed.Success, committed.ErrorMessage);
    }
}
