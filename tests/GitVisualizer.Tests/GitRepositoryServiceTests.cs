using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class GitRepositoryServiceTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public async Task RepositoryDetection_ChangesAfterInitialization()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();

        Assert.False(await service.IsRepositoryAsync(temporary.Path));

        var initialized = await service.InitializeAsync(temporary.Path, Identity);

        Assert.True(initialized.Success, initialized.ErrorMessage);
        Assert.True(await service.IsRepositoryAsync(temporary.Path));
    }

    [Fact]
    public async Task InitializeStageCommitAndHistory_RoundTrips()
    {
        using var temporary = new TemporaryDirectory();
        var log = new MemoryOperationLogStore();
        var service = new LibGitRepositoryService(new RecoveryService(), log);

        var initialized = await service.InitializeAsync(temporary.Path, Identity);
        Assert.True(initialized.Success, initialized.ErrorMessage);

        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "你好.txt"), "第一行\r\n第二行\r\n");
        var snapshot = await service.GetSnapshotAsync(temporary.Path);
        var change = Assert.Single(snapshot.Changes);
        Assert.Equal(GitChangeState.Untracked, change.State);

        var staged = await service.StageFilesAsync(temporary.Path, [change.Path]);
        Assert.True(staged.Success, staged.ErrorMessage);
        var committed = await service.CommitAsync(temporary.Path, "首次提交", Identity);
        Assert.True(committed.Success, committed.ErrorMessage);

        var history = await service.GetHistoryAsync(temporary.Path, 0, 200);
        var commit = Assert.Single(history);
        Assert.Equal("首次提交", commit.Message);
        Assert.Empty((await service.GetSnapshotAsync(temporary.Path)).Changes);
        Assert.Contains(log.Entries, entry => entry.Operation == "commit" && entry.Success);
    }

    [Fact]
    public async Task BranchMergeAndRevert_AreAvailableWithoutGitCli()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "file.txt"), "base\n");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        await File.AppendAllTextAsync(System.IO.Path.Combine(temporary.Path, "file.txt"), "feature\n");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await service.CommitAsync(temporary.Path, "feature work", Identity);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "main")).Success);
        var merged = await service.MergeAsync(temporary.Path, "feature", Identity);
        Assert.True(merged.Success, merged.ErrorMessage);

        var featureCommit = (await service.GetHistoryAsync(temporary.Path, 0, 10))
            .First(commit => commit.Message == "feature work");
        var reverted = await service.RevertAsync(temporary.Path, featureCommit.Id, Identity);
        Assert.True(reverted.Success, reverted.ErrorMessage);
        Assert.DoesNotContain("feature", await File.ReadAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "file.txt")));
    }

    [Fact]
    public async Task PushToBareRemote_ConfiguresTrackingBranch()
    {
        using var local = new TemporaryDirectory();
        using var remoteRoot = new TemporaryDirectory();
        var remotePath = System.IO.Path.Combine(remoteRoot.Path, "remote.git");
        Repository.Init(remotePath, true);
        var service = CreateService();
        await service.InitializeAsync(local.Path, Identity);
        await File.WriteAllTextAsync(System.IO.Path.Combine(local.Path, "readme.md"), "# test\n");
        await service.StageFilesAsync(local.Path, ["readme.md"]);
        await service.CommitAsync(local.Path, "initial", Identity);
        await service.AddRemoteAsync(local.Path, "origin", remotePath);

        var pushed = await service.PushAsync(local.Path, "origin", false);
        Assert.True(pushed.Success, pushed.ErrorMessage);
        var snapshot = await service.GetSnapshotAsync(local.Path);
        Assert.Equal("origin/main", snapshot.Branches.Single(branch => branch.IsCurrent).TrackedBranch);
        using var bare = new Repository(remotePath);
        Assert.NotNull(bare.Branches["main"]);
    }

    private static LibGitRepositoryService CreateService() =>
        new(new RecoveryService(), new MemoryOperationLogStore());
}
