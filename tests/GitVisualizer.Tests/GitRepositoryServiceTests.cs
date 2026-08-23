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
    public async Task Snapshot_SeparatesStagedAndLaterUnstagedChangesForSameFile()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        var path = System.IO.Path.Combine(temporary.Path, "file.txt");
        await File.WriteAllTextAsync(path, "base");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        await File.WriteAllTextAsync(path, "staged version");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await File.WriteAllTextAsync(path, "unstaged version");

        var changes = (await service.GetSnapshotAsync(temporary.Path)).Changes
            .Where(change => change.Path == "file.txt")
            .ToArray();

        Assert.Equal(2, changes.Length);
        Assert.Contains(changes, change => change.IsStaged);
        Assert.Contains(changes, change => !change.IsStaged);
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

        var deletionCheck = await service.CheckBranchDeletionAsync(temporary.Path, "feature");
        Assert.True(deletionCheck.IsMergedIntoMainline);
        var deleted = await service.DeleteBranchAsync(temporary.Path, "feature", force: false);
        Assert.True(deleted.Success, deleted.ErrorMessage);
    }

    [Fact]
    public async Task DeleteBranch_RequiresCleanWorkspaceAndWarnsWhenNotMergedIntoMainline()
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

        var cleanCheck = await service.CheckBranchDeletionAsync(temporary.Path, "feature");
        Assert.Equal("main", cleanCheck.MainlineName);
        Assert.False(cleanCheck.IsCurrent);
        Assert.False(cleanCheck.IsMainline);
        Assert.False(cleanCheck.IsMergedIntoMainline);
        Assert.Equal(0, cleanCheck.UncommittedChangeCount);

        var untrackedPath = System.IO.Path.Combine(temporary.Path, "not-committed.txt");
        await File.WriteAllTextAsync(untrackedPath, "do not delete the branch yet");
        var dirtyCheck = await service.CheckBranchDeletionAsync(temporary.Path, "feature");
        Assert.True(dirtyCheck.UncommittedChangeCount > 0);

        var blockedByDirtyWorkspace =
            await service.DeleteBranchAsync(temporary.Path, "feature", force: true);
        Assert.False(blockedByDirtyWorkspace.Success);
        Assert.Contains("未提交修改", blockedByDirtyWorkspace.ErrorMessage);
        Assert.Contains(
            (await service.GetSnapshotAsync(temporary.Path)).Branches,
            branch => branch.FriendlyName == "feature");

        File.Delete(untrackedPath);
        var blockedByUnmerged =
            await service.DeleteBranchAsync(temporary.Path, "feature", force: false);
        Assert.False(blockedByUnmerged.Success);
        Assert.Contains("尚未合并到主线 main", blockedByUnmerged.ErrorMessage);

        var forced = await service.DeleteBranchAsync(temporary.Path, "feature", force: true);
        Assert.True(forced.Success, forced.ErrorMessage);
        Assert.DoesNotContain(
            (await service.GetSnapshotAsync(temporary.Path)).Branches,
            branch => branch.FriendlyName == "feature");
    }

    [Fact]
    public async Task History_CanShowAllBranchesOrOneSelectedBranch()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "base.txt"), "base");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "feature-only.txt"),
            "feature");
        await service.StageFilesAsync(temporary.Path, ["feature-only.txt"]);
        await service.CommitAsync(temporary.Path, "feature version", Identity);

        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "main")).Success);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "main-only.txt"), "main");
        await service.StageFilesAsync(temporary.Path, ["main-only.txt"]);
        await service.CommitAsync(temporary.Path, "main version", Identity);

        var allBranches = await service.GetHistoryAsync(temporary.Path, 0, 20);
        Assert.Contains(allBranches, commit => commit.Message == "feature version");
        Assert.Contains(allBranches, commit => commit.Message == "main version");
        var snapshot = await service.GetSnapshotAsync(temporary.Path);
        var featureBranch = Assert.Single(
            snapshot.Branches,
            branch => branch.FriendlyName == "feature");
        Assert.Equal(
            allBranches.Single(commit => commit.Message == "feature version").Id,
            featureBranch.TipId);

        var featureHistory =
            await service.GetBranchHistoryAsync(temporary.Path, "feature", 0, 20);
        Assert.Contains(featureHistory, commit => commit.Message == "feature version");
        Assert.Contains(featureHistory, commit => commit.Message == "base");
        Assert.DoesNotContain(featureHistory, commit => commit.Message == "main version");
    }

    [Fact]
    public async Task BranchesArePointers_AndCheckoutAndResetMoveReferences()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "base.txt"),
            "base");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        var baseCommit = Assert.Single(
            await service.GetHistoryAsync(temporary.Path, 0, 20));
        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CreateBranchAsync(temporary.Path, "parallel")).Success);

        var sharedTip = await service.GetSnapshotAsync(temporary.Path);
        Assert.Equal(baseCommit.Id, sharedTip.Head.CommitId);
        Assert.Equal("main", sharedTip.Head.BranchName);
        Assert.False(sharedTip.Head.IsDetached);
        Assert.Equal(
            baseCommit.Id,
            sharedTip.Branches.Single(branch => branch.FriendlyName == "main").TipId);
        Assert.Equal(
            baseCommit.Id,
            sharedTip.Branches.Single(branch => branch.FriendlyName == "feature").TipId);
        Assert.Equal(
            baseCommit.Id,
            sharedTip.Branches.Single(branch => branch.FriendlyName == "parallel").TipId);

        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        var checkedOut = await service.GetSnapshotAsync(temporary.Path);
        Assert.Equal("feature", checkedOut.Head.BranchName);
        Assert.Equal(baseCommit.Id, checkedOut.Head.CommitId);

        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "feature.txt"),
            "feature");
        await service.StageFilesAsync(temporary.Path, ["feature.txt"]);
        await service.CommitAsync(temporary.Path, "feature work", Identity);
        var featureCommit = (await service.GetHistoryAsync(temporary.Path, 0, 20))
            .Single(commit => commit.Message == "feature work");

        var beforeReset = await service.GetSnapshotAsync(temporary.Path);
        Assert.Equal(featureCommit.Id, beforeReset.Head.CommitId);
        Assert.Equal(
            featureCommit.Id,
            beforeReset.Branches.Single(branch => branch.FriendlyName == "feature").TipId);
        Assert.Equal(
            baseCommit.Id,
            beforeReset.Branches.Single(branch => branch.FriendlyName == "main").TipId);

        Assert.True((
            await service.ResetAsync(
                temporary.Path,
                baseCommit.Id,
                GitResetMode.Mixed)).Success);
        var afterReset = await service.GetSnapshotAsync(temporary.Path);
        Assert.Equal(baseCommit.Id, afterReset.Head.CommitId);
        Assert.Equal("feature", afterReset.Head.BranchName);
        Assert.Equal(
            baseCommit.Id,
            afterReset.Branches.Single(branch => branch.FriendlyName == "feature").TipId);
        Assert.Contains(
            await service.GetHistoryAsync(temporary.Path, 0, 20),
            commit => commit.Id == featureCommit.Id);
    }

    [Fact]
    public async Task DeletingBranch_RemovesOnlyThePointerAndKeepsCommitHistory()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "base.txt"),
            "base");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "feature.txt"),
            "feature");
        await service.StageFilesAsync(temporary.Path, ["feature.txt"]);
        await service.CommitAsync(temporary.Path, "feature work", Identity);
        var featureCommit = (await service.GetHistoryAsync(temporary.Path, 0, 20))
            .Single(commit => commit.Message == "feature work");

        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "main")).Success);
        Assert.True((
            await service.DeleteBranchAsync(
                temporary.Path,
                "feature",
                force: true)).Success);

        var snapshot = await service.GetSnapshotAsync(temporary.Path);
        Assert.DoesNotContain(
            snapshot.Branches,
            branch => branch.FriendlyName == "feature");
        Assert.Contains(
            await service.GetHistoryAsync(temporary.Path, 0, 20),
            commit => commit.Id == featureCommit.Id);
        using var repository = new Repository(temporary.Path);
        Assert.NotNull(repository.Lookup<Commit>(featureCommit.Id));
    }

    [Fact]
    public async Task DivergentMerge_IsRenderedAsACommitWithTwoParents()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "base.txt"),
            "base");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "feature.txt"),
            "feature");
        await service.StageFilesAsync(temporary.Path, ["feature.txt"]);
        await service.CommitAsync(temporary.Path, "feature work", Identity);
        var featureTip = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "main")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "main.txt"),
            "main");
        await service.StageFilesAsync(temporary.Path, ["main.txt"]);
        await service.CommitAsync(temporary.Path, "main work", Identity);
        var mainTip = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        var merged = await service.MergeAsync(temporary.Path, "feature", Identity);
        Assert.True(merged.Success, merged.ErrorMessage);

        var history = await service.GetHistoryAsync(temporary.Path, 0, 20);
        var mergeCommit = Assert.Single(
            history,
            commit => commit.ParentIds.Count == 2);
        Assert.Contains(mainTip, mergeCommit.ParentIds);
        Assert.Contains(featureTip, mergeCommit.ParentIds);
        var snapshot = await service.GetSnapshotAsync(temporary.Path);
        Assert.Equal("main", snapshot.Head.BranchName);
        Assert.Equal(mergeCommit.Id, snapshot.Head.CommitId);
    }

    [Fact]
    public async Task HistoryEvents_ExplainBranchLifecycleAndPointerMoves()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "base.txt"),
            "base");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        var baseId = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        Assert.True((await service.CreateBranchAsync(temporary.Path, "feature")).Success);
        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "feature")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "feature.txt"),
            "feature");
        await service.StageFilesAsync(temporary.Path, ["feature.txt"]);
        await service.CommitAsync(temporary.Path, "feature work", Identity);
        var featureId = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        Assert.True((await service.CheckoutBranchAsync(temporary.Path, "main")).Success);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temporary.Path, "main.txt"),
            "main");
        await service.StageFilesAsync(temporary.Path, ["main.txt"]);
        await service.CommitAsync(temporary.Path, "main work", Identity);
        var mainId = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        Assert.True((await service.MergeAsync(temporary.Path, "feature", Identity)).Success);
        var mergeId = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;
        Assert.True((
            await service.ResetAsync(
                temporary.Path,
                mainId,
                GitResetMode.Hard)).Success);
        Assert.True((
            await service.DeleteBranchAsync(
                temporary.Path,
                "feature",
                force: true)).Success);

        var events = await service.GetHistoryEventsAsync(temporary.Path);
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.BranchCreated &&
            historyEvent.CommitId == baseId &&
            historyEvent.BranchName == "feature");
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.CommitCreated &&
            historyEvent.CommitId == featureId &&
            historyEvent.BranchName == "feature");
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.Checkout &&
            historyEvent.BranchName == "feature");
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.Merge &&
            historyEvent.CommitId == mergeId);
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.Reset &&
            historyEvent.CommitId == mainId);
        Assert.Contains(events, historyEvent =>
            historyEvent.Kind == GitHistoryEventKind.BranchDeleted &&
            historyEvent.CommitId == featureId &&
            historyEvent.BranchName == "feature");
    }

    [Fact]
    public async Task UpdatingRemote_ChangesAddressAndNameWithoutRemovingConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await service.AddRemoteAsync(
            temporary.Path,
            "origin",
            "https://example.invalid/old.git");

        var addressUpdated = await service.UpdateRemoteAsync(
            temporary.Path,
            "origin",
            "origin",
            "https://github.com/Vsing-LUO/1111.git");

        Assert.True(addressUpdated.Success, addressUpdated.ErrorMessage);
        var updatedRemote = Assert.Single(
            (await service.GetSnapshotAsync(temporary.Path)).Remotes);
        Assert.Equal("origin", updatedRemote.Name);
        Assert.Equal("https://github.com/Vsing-LUO/1111.git", updatedRemote.FetchUrl);
        Assert.Equal("https://github.com/Vsing-LUO/1111.git", updatedRemote.PushUrl);

        var renamed = await service.UpdateRemoteAsync(
            temporary.Path,
            "origin",
            "upstream",
            "git@github.com:Vsing-LUO/1111.git");

        Assert.True(renamed.Success, renamed.ErrorMessage);
        var renamedRemote = Assert.Single(
            (await service.GetSnapshotAsync(temporary.Path)).Remotes);
        Assert.Equal("upstream", renamedRemote.Name);
        Assert.Equal("git@github.com:Vsing-LUO/1111.git", renamedRemote.FetchUrl);
        Assert.Equal("git@github.com:Vsing-LUO/1111.git", renamedRemote.PushUrl);
    }

    [Fact]
    public async Task RemovingRemote_DeletesOnlyTheLocalRemoteConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await service.AddRemoteAsync(
            temporary.Path,
            "origin",
            "https://example.invalid/repository.git");

        var removed = await service.RemoveRemoteAsync(temporary.Path, "origin");

        Assert.True(removed.Success, removed.ErrorMessage);
        Assert.Empty((await service.GetSnapshotAsync(temporary.Path)).Remotes);
        Assert.True(Directory.Exists(temporary.Path));
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

        var progressUpdates = new List<GitPushProgress>();
        var pushed = await service.PushAsync(
            local.Path,
            "origin",
            false,
            progress: new CallbackProgress<GitPushProgress>(progressUpdates.Add));
        Assert.True(pushed.Success, pushed.ErrorMessage);
        Assert.Contains(
            progressUpdates,
            update => update.Stage == GitPushProgressStage.Connecting);
        Assert.Contains(
            progressUpdates,
            update => update.Stage == GitPushProgressStage.UpdatingTracking);
        var snapshot = await service.GetSnapshotAsync(local.Path);
        Assert.Equal("origin/main", snapshot.Branches.Single(branch => branch.IsCurrent).TrackedBranch);
        using var bare = new Repository(remotePath);
        Assert.NotNull(bare.Branches["main"]);

        await File.AppendAllTextAsync(System.IO.Path.Combine(local.Path, "readme.md"), "lease update\n");
        await service.StageFilesAsync(local.Path, ["readme.md"]);
        await service.CommitAsync(local.Path, "lease update", Identity);
        var forceWithMatchingLease = await service.PushAsync(local.Path, "origin", forceWithLease: true);
        Assert.True(forceWithMatchingLease.Success, forceWithMatchingLease.ErrorMessage);
        using var localRepository = new Repository(local.Path);
        Assert.Contains(localRepository.Refs, reference =>
            reference.CanonicalName.StartsWith("refs/gitvisualizer/remote-recovery/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StashPop_PreservesHiddenSafetyReference()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        var path = System.IO.Path.Combine(temporary.Path, "notes.txt");
        await File.WriteAllTextAsync(path, "base\n");
        await service.StageFilesAsync(temporary.Path, ["notes.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        await File.WriteAllTextAsync(path, "local work\n");

        var saved = await service.SaveStashAsync(temporary.Path, "work in progress", Identity);
        Assert.True(saved.Success, saved.ErrorMessage);
        var stash = Assert.Single(await service.GetStashesAsync(temporary.Path));
        Assert.Equal("work in progress", stash.Message);

        var popped = await service.ApplyStashAsync(temporary.Path, stash.Index, pop: true);

        Assert.True(popped.Success, popped.ErrorMessage);
        Assert.Empty(await service.GetStashesAsync(temporary.Path));
        Assert.Equal("local work\n", (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n"));
        using var repository = new Repository(temporary.Path);
        Assert.Contains(repository.Refs, reference =>
            reference.CanonicalName.StartsWith("refs/gitvisualizer/stash-backup/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForceWithLease_RejectsStaleRemoteWithoutChangingIt()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        using var remoteRoot = new TemporaryDirectory();
        var remotePath = System.IO.Path.Combine(remoteRoot.Path, "remote.git");
        Repository.Init(remotePath, true);
        var service = CreateService();
        await service.InitializeAsync(first.Path, Identity);
        await File.WriteAllTextAsync(System.IO.Path.Combine(first.Path, "file.txt"), "base\n");
        await service.StageFilesAsync(first.Path, ["file.txt"]);
        await service.CommitAsync(first.Path, "base", Identity);
        await service.AddRemoteAsync(first.Path, "origin", remotePath);
        Assert.True((await service.PushAsync(first.Path, "origin", false)).Success);
        using (var remote = new Repository(remotePath))
        {
            remote.Refs.UpdateTarget(remote.Refs.Head, "refs/heads/main", "set default branch");
        }

        Assert.True((await service.CloneAsync(remotePath, second.Path)).Success);
        await File.AppendAllTextAsync(System.IO.Path.Combine(second.Path, "file.txt"), "remote advance\n");
        await service.StageFilesAsync(second.Path, ["file.txt"]);
        await service.CommitAsync(second.Path, "remote advance", Identity);
        Assert.True((await service.PushAsync(second.Path, "origin", false)).Success);
        string remoteTip;
        using (var remote = new Repository(remotePath))
        {
            remoteTip = remote.Branches["main"].Tip.Id.Sha;
        }

        await File.AppendAllTextAsync(System.IO.Path.Combine(first.Path, "file.txt"), "local rewrite\n");
        await service.StageFilesAsync(first.Path, ["file.txt"]);
        await service.CommitAsync(first.Path, "local rewrite", Identity);
        var rejected = await service.PushAsync(first.Path, "origin", forceWithLease: true);

        Assert.False(rejected.Success);
        Assert.Contains("租约", rejected.ErrorMessage);
        using var unchangedRemote = new Repository(remotePath);
        Assert.Equal(remoteTip, unchangedRemote.Branches["main"].Tip.Id.Sha);
    }

    [Fact]
    public async Task BinaryConflict_TextResolutionIsBlockedWithoutChangingBytes()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        var path = System.IO.Path.Combine(temporary.Path, "asset.bin");
        await File.WriteAllBytesAsync(path, [0, 1, 2]);
        await service.StageFilesAsync(temporary.Path, ["asset.bin"]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        await service.CreateBranchAsync(temporary.Path, "feature");
        await service.CheckoutBranchAsync(temporary.Path, "feature");
        await File.WriteAllBytesAsync(path, [0, 3, 2]);
        await service.StageFilesAsync(temporary.Path, ["asset.bin"]);
        await service.CommitAsync(temporary.Path, "feature binary", Identity);
        await service.CheckoutBranchAsync(temporary.Path, "main");
        await File.WriteAllBytesAsync(path, [0, 4, 2]);
        await service.StageFilesAsync(temporary.Path, ["asset.bin"]);
        await service.CommitAsync(temporary.Path, "main binary", Identity);

        var merged = await service.MergeAsync(temporary.Path, "feature", Identity);
        Assert.True(merged.Success, merged.ErrorMessage);
        var conflict = Assert.Single(await service.GetConflictsAsync(temporary.Path));
        Assert.True(conflict.IsBinary);
        var before = await File.ReadAllBytesAsync(path);

        var blocked = await service.ResolveConflictAsync(temporary.Path, "asset.bin", "not binary");

        Assert.False(blocked.Success);
        Assert.Contains("二进制", blocked.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));

        var resolved = await service.ResolveBinaryConflictAsync(
            temporary.Path, "asset.bin", ConflictSide.Ours);

        Assert.True(resolved.Success, resolved.ErrorMessage);
        Assert.Equal(new byte[] { 0, 4, 2 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(await service.GetConflictsAsync(temporary.Path));
        Assert.NotNull(resolved.RecoveryPointId);
    }

    [Fact]
    public async Task Pull_UsesExplicitRemoteEvenWhenAnotherRemoteIsTracked()
    {
        using var local = new TemporaryDirectory();
        using var writer = new TemporaryDirectory();
        using var remotes = new TemporaryDirectory();
        var originPath = System.IO.Path.Combine(remotes.Path, "origin.git");
        var upstreamPath = System.IO.Path.Combine(remotes.Path, "upstream.git");
        Repository.Init(originPath, true);
        Repository.Init(upstreamPath, true);
        var service = CreateService();
        await service.InitializeAsync(local.Path, Identity);
        var localFile = System.IO.Path.Combine(local.Path, "file.txt");
        await File.WriteAllTextAsync(localFile, "base\n");
        await service.StageFilesAsync(local.Path, ["file.txt"]);
        await service.CommitAsync(local.Path, "base", Identity);
        await service.AddRemoteAsync(local.Path, "origin", originPath);
        await service.AddRemoteAsync(local.Path, "upstream", upstreamPath);
        Assert.True((await service.PushAsync(local.Path, "upstream", false)).Success);
        Assert.True((await service.PushAsync(local.Path, "origin", false)).Success);
        using (var upstream = new Repository(upstreamPath))
        {
            upstream.Refs.UpdateTarget(upstream.Refs.Head, "refs/heads/main", "set default branch");
        }

        Assert.True((await service.CloneAsync(upstreamPath, writer.Path)).Success);
        await File.AppendAllTextAsync(System.IO.Path.Combine(writer.Path, "file.txt"), "from upstream\n");
        await service.StageFilesAsync(writer.Path, ["file.txt"]);
        await service.CommitAsync(writer.Path, "upstream work", Identity);
        Assert.True((await service.PushAsync(writer.Path, "origin", false)).Success);

        var pulled = await service.PullAsync(
            local.Path, "upstream", "main", PullStrategy.Merge, identity: Identity);

        Assert.True(pulled.Success, pulled.ErrorMessage);
        Assert.Contains("from upstream", await File.ReadAllTextAsync(localFile));
        var snapshot = await service.GetSnapshotAsync(local.Path);
        Assert.Equal("origin/main", snapshot.Branches.Single(branch => branch.IsCurrent).TrackedBranch);
    }

    [Fact]
    public async Task TagAndRebase_WorkflowsRoundTripThroughRepositoryService()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService();
        await service.InitializeAsync(temporary.Path, Identity);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "base.txt"), "base\n");
        await service.StageFilesAsync(temporary.Path, ["base.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        await service.CreateBranchAsync(temporary.Path, "feature");
        await service.CheckoutBranchAsync(temporary.Path, "feature");
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "feature.txt"), "feature\n");
        await service.StageFilesAsync(temporary.Path, ["feature.txt"]);
        await service.CommitAsync(temporary.Path, "feature", Identity);
        await service.CheckoutBranchAsync(temporary.Path, "main");
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, "main.txt"), "main\n");
        await service.StageFilesAsync(temporary.Path, ["main.txt"]);
        await service.CommitAsync(temporary.Path, "main advance", Identity);
        var mainTip = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;
        await service.CheckoutBranchAsync(temporary.Path, "feature");

        var rebased = await service.RebaseOntoAsync(temporary.Path, "main", identity: Identity);
        Assert.True(rebased.Success, rebased.ErrorMessage);
        using (var repository = new Repository(temporary.Path))
        {
            Assert.Equal(mainTip, repository.Head.Tip.Parents.Single().Id.Sha);
        }

        var created = await service.CreateTagAsync(temporary.Path, "v-test");
        Assert.True(created.Success, created.ErrorMessage);
        Assert.Contains((await service.GetSnapshotAsync(temporary.Path)).Tags, tag => tag.Name == "v-test");
        var deleted = await service.DeleteTagAsync(temporary.Path, "v-test");
        Assert.True(deleted.Success, deleted.ErrorMessage);
        Assert.DoesNotContain((await service.GetSnapshotAsync(temporary.Path)).Tags, tag => tag.Name == "v-test");
    }

    private static LibGitRepositoryService CreateService() =>
        new(new RecoveryService(), new MemoryOperationLogStore());
}
