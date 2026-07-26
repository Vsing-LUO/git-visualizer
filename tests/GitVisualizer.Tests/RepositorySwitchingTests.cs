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
        await CreateRepositoryAsync(git, firstPath, "第一个仓库提交");
        await CreateRepositoryAsync(git, secondPath, "第二个仓库提交");

        using var viewModel = new MainWindowViewModel(
            git,
            new LibGitDiffService(),
            new LibGitIndexPatchService(log),
            new RepositoryWatcherFactory(),
            new FileWorkspaceService(),
            new MemorySettingsStore(),
            log,
            recovery,
            new MemoryCredentialVault());

        Assert.True(await viewModel.OpenRepositoryAsync(firstPath));
        Assert.Equal("第一个仓库提交", Assert.Single(viewModel.History).Message);

        Assert.True(await viewModel.OpenRepositoryAsync(secondPath));

        Assert.Equal(Path.GetFullPath(secondPath), viewModel.ActiveRepositoryPath);
        Assert.Equal(Path.GetFullPath(secondPath), viewModel.SelectedRepository);
        Assert.Equal("第二个仓库提交", Assert.Single(viewModel.History).Message);
        Assert.DoesNotContain(viewModel.History, commit => commit.Message == "第一个仓库提交");
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
