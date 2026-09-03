using GitVisualizer.App.ViewModels;
using GitVisualizer.App.Services;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class RepositorySwitchingTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public async Task EmptyRepository_DoesNotShowHistoryCompleteIndicator()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "empty");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await git.InitializeAsync(repositoryPath, Identity);

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
        Assert.Empty(viewModel.History);
        Assert.True(viewModel.HasLoadedHistory);
        Assert.False(viewModel.HasMoreHistory);
        Assert.False(viewModel.IsHistoryComplete);
    }

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
        Assert.Equal(1, viewModel.SelectedRightTabIndex);
        Assert.Null(viewModel.CurrentDocument);
        Assert.Equal("第一个仓库提交", Assert.Single(viewModel.History).Message);
        Assert.True(viewModel.HasLoadedHistory);
        Assert.False(viewModel.HasMoreHistory);
        Assert.True(viewModel.IsHistoryComplete);

        var textFile = Assert.Single(
            viewModel.FileTree,
            item => item.Name == "内容.txt");
        await viewModel.SelectFileAsync(textFile);
        Assert.Equal(1, viewModel.SelectedRightTabIndex);
        Assert.NotNull(viewModel.CurrentDocument);

        await viewModel.SelectCommitAsync(Assert.Single(viewModel.History));
        Assert.Equal(2, viewModel.SelectedRightTabIndex);

        await File.AppendAllTextAsync(Path.Combine(firstPath, "内容.txt"), "已修改");
        await viewModel.RefreshAsync();
        await viewModel.SelectChangeAsync(Assert.Single(
            viewModel.UnstagedChanges,
            change => change.Path == "内容.txt"));
        Assert.Equal(0, viewModel.SelectedRightTabIndex);
        Assert.True(viewModel.ShowWorkingDiffCards);
        Assert.NotEmpty(viewModel.DiffRegions);
        Assert.True(viewModel.CanShowRawDiff);
        Assert.False(viewModel.ShowRawDiff);
        viewModel.ToggleRawDiff();
        Assert.True(viewModel.ShowRawDiff);
        Assert.Equal("返回易懂说明", viewModel.RawDiffToggleText);
        await viewModel.SelectChangeAsync(null);
        Assert.False(viewModel.ShowRawDiff);
        Assert.False(viewModel.CanShowRawDiff);

        Assert.True(await viewModel.OpenRepositoryAsync(secondPath));

        Assert.Equal(Path.GetFullPath(secondPath), viewModel.ActiveRepositoryPath);
        Assert.Equal(1, viewModel.SelectedRightTabIndex);
        Assert.Null(viewModel.CurrentDocument);
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
        Assert.Equal(Path.GetFullPath(secondPath), settingsStore.Settings.LastRepository);
        Assert.True(viewModel.HasRepository);
        Assert.Equal(Path.GetFullPath(secondPath), viewModel.ActiveRepositoryPath);
        Assert.Equal(Path.GetFullPath(secondPath), viewModel.SelectedRepository);
        Assert.Equal("第二个仓库提交", Assert.Single(viewModel.History).Message);
        Assert.True(Directory.Exists(Path.Combine(firstPath, ".git")));
        Assert.True(File.Exists(Path.Combine(firstPath, "内容.txt")));

        Assert.True(await viewModel.OpenRepositoryAsync(firstPath));
        Assert.Contains(
            viewModel.RecentRepositories,
            path => path.Equals(firstPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemovingLastRepository_RestoresInitialEmptyRepositoryView()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "only-repository");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        var settingsStore = new MemorySettingsStore();
        await CreateRepositoryAsync(git, repositoryPath, "唯一仓库提交");

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

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.NotEmpty(viewModel.History);
        Assert.NotEmpty(viewModel.Branches);
        Assert.NotEmpty(viewModel.FileTree);

        Assert.True(await viewModel.RemoveRecentRepositoryAsync(repositoryPath));

        Assert.Empty(viewModel.RecentRepositories);
        Assert.Empty(settingsStore.Settings.RecentRepositories);
        Assert.Null(settingsStore.Settings.LastRepository);
        Assert.False(viewModel.HasRepository);
        Assert.Equal(string.Empty, viewModel.ActiveRepositoryPath);
        Assert.Null(viewModel.SelectedRepository);
        Assert.Equal("未打开仓库", viewModel.CurrentBranch);
        Assert.Equal("拖入文件夹，或点击“打开仓库”开始", viewModel.StatusText);
        Assert.Empty(viewModel.History);
        Assert.Empty(viewModel.Branches);
        Assert.Empty(viewModel.FileTree);
        Assert.Empty(viewModel.UnstagedChanges);
        Assert.Empty(viewModel.StagedChanges);
        Assert.Null(viewModel.Head);
        Assert.Null(viewModel.CurrentDocument);
        Assert.False(viewModel.CanModifyFileTree);
        Assert.True(Directory.Exists(Path.Combine(repositoryPath, ".git")));
    }

    [Fact]
    public async Task CommitAndAmend_ClearMessageAndReturnToWorkingTree()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "commit-completion");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "基础提交");
        await File.AppendAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "新增内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["内容.txt"])).Success);

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

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.True(await viewModel.SelectCommitAsync(Assert.Single(viewModel.History)));
        Assert.True(viewModel.IsBrowsingHistoricalCommit);
        viewModel.CommitMessage = "创建新提交";

        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.CommitMessage);
        Assert.Null(viewModel.SelectedCommit);
        Assert.False(viewModel.IsBrowsingHistoricalCommit);
        Assert.Equal("工作区", viewModel.FileTreeContextText);
        Assert.Equal(1, viewModel.SelectedRightTabIndex);
        Assert.Equal("创建新提交", viewModel.History[0].Message);

        await File.AppendAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "修订内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["内容.txt"])).Success);
        await viewModel.RefreshAsync();
        Assert.True(await viewModel.SelectCommitAsync(viewModel.History[^1]));
        Assert.True(viewModel.IsBrowsingHistoricalCommit);
        viewModel.CommitMessage = "修订后的提交";

        await viewModel.AmendCommand.ExecuteAsync(null);

        Assert.Equal("上一提交已修改", viewModel.StatusText);
        Assert.Equal(string.Empty, viewModel.CommitMessage);
        Assert.Null(viewModel.SelectedCommit);
        Assert.False(viewModel.IsBrowsingHistoricalCommit);
        Assert.Equal("工作区", viewModel.FileTreeContextText);
        Assert.Equal(1, viewModel.SelectedRightTabIndex);
        Assert.Equal(
            "修订后的提交",
            Assert.Single(
                viewModel.History,
                commit => commit.Id == viewModel.Head?.CommitId).Message);
    }

    [Fact]
    public async Task FailedCommit_KeepsMessageForRetry()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "failed-commit");
        Directory.CreateDirectory(repositoryPath);

        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "基础提交");

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

        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        viewModel.CommitMessage = "失败后保留此说明";

        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal("失败后保留此说明", viewModel.CommitMessage);
    }

    [Theory]
    [InlineData(2, "second")]
    [InlineData(1, "first")]
    [InlineData(0, "second")]
    public void RepositoryRemovalPrefersPreviousVisibleItem(
        int removedIndex,
        string expected)
    {
        string[] repositories = ["first", "second", "third"];

        Assert.Equal(
            expected,
            MainWindowViewModel.SelectRepositoryAfterRemoval(
                repositories,
                removedIndex));
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
    public async Task CheckoutBranch_ReloadsOpenEditorAndSupportsSaveStageCommit()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "branch-editor");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "main 内容");
        Assert.True((await git.CreateBranchAsync(repositoryPath, "feature")).Success);
        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "feature")).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "feature 内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["内容.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "feature 初始内容", Identity)).Success);
        Assert.True((await git.CheckoutBranchAsync(repositoryPath, "main")).Success);

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
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.True(await viewModel.SelectFileAsync(Assert.Single(
            viewModel.FileTree, item => item.Name == "内容.txt")));
        Assert.Equal("main 内容", viewModel.EditorText);

        var feature = Assert.Single(
            viewModel.Branches, branch => branch.FriendlyName == "feature");
        var checkout = await viewModel.CheckoutBranchAsync(feature);

        Assert.True(checkout.Success, checkout.ErrorMessage);
        Assert.Equal("feature 内容", viewModel.EditorText);
        Assert.Equal("feature 内容", viewModel.CurrentDocument?.Text);
        Assert.False(viewModel.HasUnsavedEditorChanges);

        viewModel.EditorText = "feature 编辑并提交";
        await viewModel.SaveAndStageEditorCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasUnsavedEditorChanges);
        Assert.Equal("feature 编辑并提交", await File.ReadAllTextAsync(
            Path.Combine(repositoryPath, "内容.txt")));
        Assert.Contains(viewModel.StagedChanges, change => change.Path == "内容.txt");

        viewModel.CommitMessage = "编辑器分支提交";
        await viewModel.CommitCommand.ExecuteAsync(null);
        Assert.Contains(viewModel.History, commit => commit.Message == "编辑器分支提交");
    }

    [Fact]
    public async Task ExplicitSave_CancelsDelayedDraftSoItCannotReappear()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "draft-save-race");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "原始内容");
        var drafts = new RecordingDraftStore();
        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault(),
            draftStore: drafts);
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.True(await viewModel.SelectFileAsync(Assert.Single(
            viewModel.FileTree, item => item.Name == "内容.txt")));

        viewModel.EditorText = "立即保存，不应复活草稿";
        await viewModel.SaveEditorCommand.ExecuteAsync(null);
        await Task.Delay(900);

        Assert.False(viewModel.HasUnsavedEditorChanges);
        Assert.Equal(0, drafts.SaveCount);
        Assert.True(drafts.DeleteCount >= 1);
    }

    [Fact]
    public async Task ExternalChange_CanBeExplicitlyOverwrittenOrReloaded()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "external-editor-change");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "磁盘原文");
        var interaction = new RecordingEditorInteractionService();
        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault(),
            editorInteraction: interaction);
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.True(await viewModel.SelectFileAsync(Assert.Single(
            viewModel.FileTree, item => item.Name == "内容.txt")));
        var path = Path.Combine(repositoryPath, "内容.txt");

        viewModel.EditorText = "保留编辑器内容";
        await File.WriteAllTextAsync(path, "外部内容");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        interaction.ExternalChangeAction = EditorSafetyAction.Save;
        await viewModel.SaveEditorCommand.ExecuteAsync(null);

        Assert.Equal("保留编辑器内容", await File.ReadAllTextAsync(path));
        Assert.False(viewModel.HasUnsavedEditorChanges);

        viewModel.EditorText = "这次放弃编辑器内容";
        await File.WriteAllTextAsync(path, "采用新的磁盘内容");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
        interaction.ExternalChangeAction = EditorSafetyAction.Discard;
        await viewModel.SaveEditorCommand.ExecuteAsync(null);

        Assert.Equal("采用新的磁盘内容", viewModel.EditorText);
        Assert.Equal("采用新的磁盘内容", viewModel.CurrentDocument?.Text);
        Assert.False(viewModel.HasUnsavedEditorChanges);
    }

    [Fact]
    public async Task ConcurrentTransitions_ShowOnlyOneUnsavedChangesPrompt()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "transition-race");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "第一个文件");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "第二个.txt"), "第二个文件");
        var interaction = new BlockingEditorInteractionService();
        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault(),
            editorInteraction: interaction);
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        Assert.True(await viewModel.SelectFileAsync(Assert.Single(
            viewModel.FileTree, item => item.Name == "内容.txt")));
        viewModel.EditorText = "尚未保存的并发编辑";
        var secondFile = Assert.Single(viewModel.FileTree, item => item.Name == "第二个.txt");

        var switchTask = viewModel.SelectFileAsync(secondFile);
        await interaction.PromptShown;
        var closeTask = viewModel.PrepareForCloseAsync();
        await Task.Delay(50);
        Assert.Equal(1, interaction.PromptCount);

        interaction.Resolve(EditorSafetyAction.Discard);
        Assert.True(await switchTask);
        Assert.True(await closeTask);
        Assert.Equal(1, interaction.PromptCount);
        Assert.Equal("第二个文件", viewModel.EditorText);
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
        Assert.DoesNotContain(
            viewModel.UnstagedChanges,
            change => change.Path.Equals("内容.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            viewModel.StagedChanges,
            change => change.Path.Equals("内容.txt", StringComparison.OrdinalIgnoreCase));
        var notification = Assert.Single(conflictNotifications);
        Assert.Equal(1, notification.ConflictCount);
        Assert.Equal("合并", notification.OperationName);

        var conflictPath = Path.Combine(repositoryPath, "内容.txt");
        var discarded = await git.DiscardFilesAsync(repositoryPath, ["内容.txt"]);
        Assert.False(discarded.Success);
        Assert.Contains("冲突文件", discarded.ErrorMessage);
        Assert.True(File.Exists(conflictPath));
        Assert.Single(await git.GetConflictsAsync(repositoryPath));

        await viewModel.RefreshAsync();
        Assert.Single(conflictNotifications);

        viewModel.UseConflictSide(ConflictSide.Both);
        Assert.Contains("来自 main", viewModel.ConflictResultText);
        Assert.Contains("来自 feature", viewModel.ConflictResultText);
        var editedResult = viewModel.ConflictResultText;
        await viewModel.RefreshAsync();
        Assert.Equal(editedResult, viewModel.ConflictResultText);
        Assert.Single(conflictNotifications);
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

    [Fact]
    public async Task SelectedFiles_AreStagedAndUnstagedWithoutChangingOtherSelections()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "selected-files");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "初始内容");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "第二个.txt"), "第二个初始内容");
        Assert.True((await git.StageFilesAsync(repositoryPath, ["第二个.txt"])).Success);
        Assert.True((await git.CommitAsync(repositoryPath, "增加第二个文件", Identity)).Success);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "内容.txt"), "只暂存这个修改");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "第二个.txt"), "保持未暂存");

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
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        var selected = Assert.Single(
            viewModel.UnstagedChanges,
            change => change.Path == "内容.txt");

        var staged = await viewModel.StageSelectedFilesAsync([selected]);

        Assert.NotNull(staged);
        Assert.True(staged.Success, staged.ErrorMessage);
        Assert.Contains(viewModel.StagedChanges, change => change.Path == "内容.txt");
        Assert.Contains(viewModel.UnstagedChanges, change => change.Path == "第二个.txt");
        Assert.DoesNotContain(viewModel.StagedChanges, change => change.Path == "第二个.txt");

        var stagedSelection = Assert.Single(
            viewModel.StagedChanges,
            change => change.Path == "内容.txt");
        var unstaged = await viewModel.UnstageSelectedFilesAsync([stagedSelection]);

        Assert.NotNull(unstaged);
        Assert.True(unstaged.Success, unstaged.ErrorMessage);
        Assert.Empty(viewModel.StagedChanges);
        Assert.Contains(viewModel.UnstagedChanges, change => change.Path == "内容.txt");
        Assert.Contains(viewModel.UnstagedChanges, change => change.Path == "第二个.txt");
    }

    [Fact]
    public async Task CloseGuardSupportsCancelDiscardAndSaveWithoutPrematureStateLoss()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryPath = Path.Combine(temporary.Path, "close-guard");
        Directory.CreateDirectory(repositoryPath);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        await CreateRepositoryAsync(git, repositoryPath, "磁盘原文");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "第二个.txt"), "第二个文件");
        var interaction = new RecordingEditorInteractionService();
        var drafts = new RecordingDraftStore();
        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new WindowsShellNewFileService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault(),
            draftStore: drafts,
            editorInteraction: interaction);
        Assert.True(await viewModel.OpenRepositoryAsync(repositoryPath));
        await viewModel.SelectFileAsync(Assert.Single(
            viewModel.FileTree, item => item.Name == "内容.txt"));

        viewModel.EditorText = "尚未保存";
        interaction.Action = EditorSafetyAction.Cancel;
        var secondFile = Assert.Single(viewModel.FileTree, item => item.Name == "第二个.txt");
        Assert.False(await viewModel.SelectFileAsync(secondFile));
        Assert.Equal("内容.txt", Path.GetFileName(viewModel.CurrentDocument?.Path));
        Assert.False(await viewModel.SelectCommitAsync(Assert.Single(viewModel.History)));
        Assert.Null(viewModel.SelectedCommit);
        Assert.False(await viewModel.PrepareForCloseAsync());
        Assert.Equal("尚未保存", viewModel.EditorText);
        Assert.True(viewModel.HasUnsavedEditorChanges);
        Assert.Equal("磁盘原文", await File.ReadAllTextAsync(Path.Combine(repositoryPath, "内容.txt")));

        interaction.Action = EditorSafetyAction.Discard;
        Assert.True(await viewModel.PrepareForCloseAsync());
        Assert.Equal("磁盘原文", viewModel.EditorText);
        Assert.False(viewModel.HasUnsavedEditorChanges);
        Assert.Equal(1, drafts.DeleteCount);

        viewModel.EditorText = "确认保存";
        interaction.Action = EditorSafetyAction.Save;
        Assert.True(await viewModel.PrepareForCloseAsync());
        Assert.Equal("确认保存", await File.ReadAllTextAsync(Path.Combine(repositoryPath, "内容.txt")));
        Assert.False(viewModel.HasUnsavedEditorChanges);
    }

    private sealed class RecordingEditorInteractionService : IEditorInteractionService
    {
        public EditorSafetyAction Action { get; set; } = EditorSafetyAction.Cancel;
        public EditorSafetyAction ExternalChangeAction { get; set; } = EditorSafetyAction.Cancel;
        public Task<EditorSafetyAction> ResolveUnsavedChangesAsync(
            TextDocument document, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(Action);
        public Task<EditorSafetyAction> ResolveDraftAsync(
            EditorDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(Action);
        public Task<EditorSafetyAction> ResolveExternalChangeAsync(
            TextDocument document, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalChangeAction);
    }

    private sealed class RecordingDraftStore : IEditorDraftStore
    {
        public int DeleteCount { get; private set; }
        public int SaveCount { get; private set; }
        public Task<EditorDraft?> LoadAsync(string repositoryPath, string documentPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<EditorDraft?>(null);
        public Task SaveAsync(EditorDraft draft, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string repositoryPath, string documentPath, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
        public Task MoveAsync(string repositoryPath, string oldDocumentPath, string newDocumentPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingEditorInteractionService : IEditorInteractionService
    {
        private readonly TaskCompletionSource<EditorSafetyAction> resolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource promptShown =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PromptCount { get; private set; }
        public Task PromptShown => promptShown.Task;

        public Task<EditorSafetyAction> ResolveUnsavedChangesAsync(
            TextDocument document, string reason, CancellationToken cancellationToken = default)
        {
            PromptCount++;
            promptShown.TrySetResult();
            return resolution.Task.WaitAsync(cancellationToken);
        }

        public Task<EditorSafetyAction> ResolveDraftAsync(
            EditorDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(EditorSafetyAction.Cancel);

        public Task<EditorSafetyAction> ResolveExternalChangeAsync(
            TextDocument document, CancellationToken cancellationToken = default) =>
            Task.FromResult(EditorSafetyAction.Cancel);

        public void Resolve(EditorSafetyAction action) => resolution.TrySetResult(action);
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
