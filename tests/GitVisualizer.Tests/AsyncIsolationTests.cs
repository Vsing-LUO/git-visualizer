// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class AsyncIsolationTests
{
    [Theory]
    [InlineData("commit")]
    [InlineData("branch")]
    [InlineData("file")]
    [InlineData("change")]
    public async Task OldRestoreCompletionPreservesNewRepositorySelection(string selection)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var point = await fixture.Recovery.CreateAsync(fixture.A, "saved");
        var delay = new Delayed<GitOperationResult>();
        CancellationToken token = default;
        fixture.RecoveryProxy.Intercept = (method, args) =>
        {
            if (method.Name != nameof(IRecoveryService.RestoreAsync)) return null;
            token = (CancellationToken)args[1]!;
            return delay.Wait();
        };
        var pending = vm.RestoreRecoveryPointAsync(point);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);
        Assert.True(await vm.OpenRepositoryAsync(fixture.B));
        Assert.True(token.IsCancellationRequested);
        if (selection == "commit") await vm.SelectCommitAsync(vm.History.First());
        if (selection == "branch") await vm.SelectBranchAsync(vm.Branches.First(branch => branch.FriendlyName == "side"));
        if (selection == "file") await vm.SelectFileAsync(vm.FileTree.First(file => file.Name == "a.txt"));
        if (selection == "change") await vm.SelectChangeAsync(vm.UnstagedChanges.First());
        var commit = vm.SelectedCommit; var branch = vm.SelectedBranch;
        var change = vm.SelectedChange; var document = vm.CurrentDocument;
        var history = vm.History.ToArray(); var tree = vm.FileTree.ToArray();
        var status = vm.StatusText; var details = vm.DetailsText;
        int refreshes = 0;
        fixture.GitProxy.Intercept = (method, _) =>
        {
            if (method.Name == nameof(IGitRepositoryService.GetSnapshotAsync)) refreshes++;
            return null;
        };
        var result = GitOperationResult.Ok("restore", "old restore completed", "restore");
        delay.Complete(result);
        Assert.Same(result, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, refreshes);
        Assert.Same(commit, vm.SelectedCommit); Assert.Same(branch, vm.SelectedBranch);
        Assert.Same(change, vm.SelectedChange); Assert.Same(document, vm.CurrentDocument);
        Assert.Equal(history, vm.History); Assert.Equal(tree, vm.FileTree);
        Assert.Equal(status, vm.StatusText); Assert.Equal(details, vm.DetailsText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRefreshCannotContinueAfterSessionEnds(bool close)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var point = await fixture.Recovery.CreateAsync(fixture.A, "saved");
        var result = GitOperationResult.Ok("restore", "restored", "restore");
        fixture.RecoveryProxy.Intercept = (method, _) => method.Name == nameof(IRecoveryService.RestoreAsync) ? Task.FromResult(result) : null;
        var snapshot = await fixture.Git.GetSnapshotAsync(fixture.A);
        var delay = new Delayed<RepositorySnapshot>();
        fixture.GitProxy.Intercept = (method, args) => method.Name == nameof(IGitRepositoryService.GetSnapshotAsync) && (string)args[0]! == fixture.A ? delay.Wait() : null;
        var pending = vm.RestoreRecoveryPointAsync(point);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (close) vm.Dispose();
        else
        {
            Assert.True(await vm.OpenRepositoryAsync(fixture.B));
            await vm.SelectCommitAsync(vm.History.First());
        }
        var history = vm.History.ToArray(); var selected = vm.SelectedCommit;
        var status = vm.StatusText; var details = vm.DetailsText;
        delay.Complete(snapshot);
        Assert.Same(result, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(history, vm.History); Assert.Same(selected, vm.SelectedCommit);
        Assert.Equal(status, vm.StatusText); Assert.Equal(details, vm.DetailsText);
    }

    [Theory]
    [InlineData("write-lock", false)]
    [InlineData("write-lock", true)]
    [InlineData("preflight-complete", false)]
    [InlineData("preflight-complete", true)]
    [InlineData("restore-file", false)]
    [InlineData("restore-file", true)]
    public async Task RestoreSessionCancellationPreservesExecutionOutcome(string stage, bool close)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var point = await fixture.Recovery.CreateAsync(fixture.A, "saved");
        File.WriteAllText(Path.Combine(fixture.A, "a.txt"), "valuable current contents");
        string head; string[] references; byte[] index;
        using (var repository = new Repository(fixture.A))
        {
            head = repository.Head.CanonicalName;
            references = repository.Refs.Select(reference => reference.CanonicalName).Order().ToArray();
            index = File.ReadAllBytes(Path.Combine(repository.Info.Path, "index"));
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var gate = GitServiceSupport.LockFor(fixture.A);
        if (stage == "write-lock") await gate.WaitAsync();
        fixture.Recovery.Checkpoint = (current, _) =>
        {
            if (current != stage) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Restore checkpoint was not released.");
        };
        CancellationToken token = default;
        fixture.RecoveryProxy.Intercept = (method, args) =>
        {
            if (method.Name != nameof(IRecoveryService.RestoreAsync)) return null;
            token = (CancellationToken)args[1]!;
            return Task.Run(async () =>
            {
                var restore = fixture.Recovery.RestoreAsync((RecoveryPoint)args[0]!, token);
                if (stage == "write-lock") entered.TrySetResult();
                return await restore;
            });
        };
        var pending = vm.RestoreRecoveryPointAsync(point);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.True(token.CanBeCanceled); Assert.False(token.IsCancellationRequested);
            // Opening B also prunes recovery references under the shared recovery gate.
            // Resume the paused restore as soon as switching cancels A, before awaiting B.
            using var cancellationRelease = token.Register(release.Set);
            if (close) vm.Dispose(); else Assert.True(await vm.OpenRepositoryAsync(fixture.B));
            Assert.True(token.IsCancellationRequested);
            var status = vm.StatusText;
            release.Set();
            // The write-lock case must finish while the original lock is still held.
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(status, vm.StatusText);
            if (stage == "restore-file")
            {
                Assert.Equal(GitOperationOutcome.PartiallyCompleted, result.Outcome);
                Assert.Equal("worktree", result.ExecutionStage);
                var safety = Assert.Single(await fixture.Recovery.ListAsync(fixture.A), item => item.Id == result.RecoveryPointId);
                Assert.True(File.Exists(safety.ArchivePath + ".pin"));
                var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.ExecutionRecordPath!))!;
                Assert.True(journal["repositoryMayHaveChanged"]!.GetValue<bool>());
                Assert.Equal(safety.Id, journal["safetyPointId"]!.GetValue<string>());
                Assert.Equal("worktree-failed", journal["stage"]!.GetValue<string>());
            }
            else
            {
                Assert.Equal(GitOperationOutcome.CanceledBeforeExecution, result.Outcome);
                Assert.Null(result.RecoveryPointId);
                Assert.Equal("valuable current contents", File.ReadAllText(Path.Combine(fixture.A, "a.txt")));
                using var repository = new Repository(fixture.A);
                Assert.Equal(head, repository.Head.CanonicalName);
                Assert.Equal(index, File.ReadAllBytes(Path.Combine(repository.Info.Path, "index")));
                Assert.Equal(references, repository.Refs.Select(reference => reference.CanonicalName).Order().ToArray());
            }
        }
        finally
        {
            release.Set();
            if (stage == "write-lock") gate.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ContinuousSwitchesDiscardOldResultsEvenWhenReturningToSamePath()
    {
        using var fixture = new Fixture();
        var a = await fixture.Git.GetSnapshotAsync(fixture.A);
        var b = await fixture.Git.GetSnapshotAsync(fixture.B);
        var oldA = new Delayed<RepositorySnapshot>(); var oldB = new Delayed<RepositorySnapshot>();
        bool firstA = true;
        CancellationToken oldToken = default;
        fixture.GitProxy.Intercept = (method, args) =>
        {
            if (method.Name != nameof(IGitRepositoryService.GetSnapshotAsync)) return null;
            if ((string)args[0]! == fixture.A && firstA) { firstA = false; oldToken = (CancellationToken)args[1]!; return oldA.Wait(); }
            if ((string)args[0]! == fixture.B) return oldB.Wait();
            return null;
        };
        using var vm = fixture.ViewModel();
        var first = vm.OpenRepositoryAsync(fixture.A); await oldA.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = vm.OpenRepositoryAsync(fixture.B); await oldB.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var status = vm.StatusText;
        oldB.Complete(b); oldA.Complete(a);
        Assert.False(await second); Assert.False(await first);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(fixture.A, vm.ActiveRepositoryPath);
        Assert.All(vm.History, commit => Assert.StartsWith("A-", commit.Message));
        Assert.Equal(status, vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("conflicts")]
    public async Task OldRepositoryQueriesCannotWriteIntoNewSession(string query)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var delay = new Delayed<DiffPresentation>();
        var conflictDelay = new Delayed<IReadOnlyList<ConflictFile>>();
        Task pending;
        DiffPresentation? presentation = null;
        if (query == "diff")
        {
            var change = vm.UnstagedChanges.First(item => item.Path == "a.txt");
            presentation = await new LibGitDiffService().GetWorkingDiffPresentationAsync(fixture.A, change.Path, false);
            fixture.DiffProxy.Intercept = (method, _) => method.Name == nameof(IDiffService.GetWorkingDiffPresentationAsync) ? delay.Wait() : null;
            pending = vm.SelectChangeAsync(change);
            await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            fixture.GitProxy.Intercept = (method, args) => method.Name == nameof(IGitRepositoryService.GetConflictsAsync) && (string)args[0]! == fixture.A ? conflictDelay.Wait() : null;
            pending = vm.RefreshAsync();
            await conflictDelay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(await vm.OpenRepositoryAsync(fixture.B));
        var status = vm.StatusText;
        if (query == "diff") delay.Complete(presentation!);
        else conflictDelay.Complete(new[] { new ConflictFile("old.txt", "old", "old", "old", "old", false, false) });
        await pending;
        Assert.Equal(fixture.B, vm.ActiveRepositoryPath);
        Assert.Null(vm.SelectedChange); Assert.Empty(vm.Conflicts);
        Assert.Equal(status, vm.StatusText);
        Assert.All(vm.History, commit => Assert.StartsWith("B-", commit.Message));
    }

    [Fact]
    public async Task BranchHistoryCompletingLastCannotReplaceNewBranch()
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var main = vm.Branches.First(branch => branch.IsCurrent);
        var side = vm.Branches.First(branch => branch.FriendlyName == "side");
        var oldHistory = await fixture.Git.GetBranchHistoryAsync(fixture.A, main.FriendlyName, 0, 201);
        var delay = new Delayed<IReadOnlyList<CommitNode>>();
        fixture.GitProxy.Intercept = (method, args) => method.Name == nameof(IGitRepositoryService.GetBranchHistoryAsync) && (string)args[1]! == main.FriendlyName ? delay.Wait() : null;
        var old = vm.SelectBranchAsync(main); await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await vm.SelectBranchAsync(side));
        var status = vm.StatusText;
        delay.Complete(oldHistory); Assert.False(await old);
        Assert.Equal("side", vm.SelectedHistoryBranchName);
        Assert.Equal(side.TipId, vm.SelectedCommit!.Id);
        Assert.Contains(vm.FileTree, item => item.Name == "side.txt");
        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public async Task CommitTreeCompletingLastCannotReplaceNewSelection()
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var older = vm.History.Single(commit => commit.Message == "A-base");
        var newer = vm.History.Single(commit => commit.Message == "A-side");
        var tree = await fixture.Git.GetCommitTreeAsync(fixture.A, older.Id);
        var delay = new Delayed<IReadOnlyList<CommitTreeEntry>>();
        fixture.GitProxy.Intercept = (method, args) => method.Name == nameof(IGitRepositoryService.GetCommitTreeAsync) && (string)args[1]! == older.Id ? delay.Wait() : null;
        var old = vm.SelectCommitAsync(older); await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await vm.SelectCommitAsync(newer));
        delay.Complete(tree); Assert.False(await old);
        Assert.Equal(newer.Id, vm.SelectedCommit!.Id);
        Assert.Contains(vm.FileTree, item => item.Name == "side.txt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedFileReadCannotOverwriteNewDocumentOrClosedWindow(bool close)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var a = vm.FileTree.Single(file => file.Name == "a.txt");
        var b = vm.FileTree.Single(file => file.Name == "b.txt");
        var document = await new FileWorkspaceService().OpenTextAsync(a.FullPath);
        var delay = new Delayed<TextDocument>();
        fixture.FilesProxy.Intercept = (method, args) => method.Name == nameof(IFileWorkspaceService.OpenTextAsync) && (string)args[0]! == a.FullPath ? delay.Wait() : null;
        var old = vm.SelectFileAsync(a); await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (close) vm.Dispose(); else Assert.True(await vm.SelectFileAsync(b));
        var status = vm.StatusText; var text = vm.EditorText;
        delay.Complete(document); Assert.False(await old);
        Assert.Equal(text, vm.EditorText); Assert.Equal(status, vm.StatusText);
        if (!close) Assert.Equal(b.FullPath, vm.CurrentDocument!.Path);
    }

    [Fact]
    public async Task OldWriteCompletionDoesNotReloadOrReportIntoNewRepository()
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        var delay = new Delayed<GitOperationResult>();
        fixture.GitProxy.Intercept = (method, _) => method.Name == nameof(IGitRepositoryService.CreateBranchAsync) ? delay.Wait() : null;
        var old = vm.CreateBranchAsync("created-on-A"); await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await vm.OpenRepositoryAsync(fixture.B));
        var status = vm.StatusText;
        var actual = await fixture.Git.CreateBranchAsync(fixture.A, "created-on-A");
        delay.Complete(actual);
        Assert.True((await old).Success);
        Assert.Equal(status, vm.StatusText);
        Assert.DoesNotContain(vm.Branches, branch => branch.FriendlyName == "created-on-A");
        using var repository = new Repository(fixture.A); Assert.NotNull(repository.Branches["created-on-A"]);
        using var other = new Repository(fixture.B); Assert.Null(other.Branches["created-on-A"]);
    }

    [Theory]
    [InlineData("message", false, false)]
    [InlineData("selection", false, false)]
    [InlineData("status", false, false)]
    [InlineData("message", true, false)]
    [InlineData("selection", true, false)]
    [InlineData("status", true, false)]
    [InlineData("message", false, true)]
    [InlineData("selection", false, true)]
    [InlineData("status", false, true)]
    [InlineData("message", true, true)]
    [InlineData("selection", true, true)]
    [InlineData("status", true, true)]
    public async Task CommitRefreshCannotChangeStateAfterSessionEnds(string field, bool amend, bool close)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        Assert.True((await fixture.Git.StageFilesAsync(fixture.A, ["a.txt"])).Success);
        using var other = new Repository(fixture.B);
        var otherHead = other.Head.Tip.Id;
        vm.CommitMessage = "commit on A";
        var delay = new Delayed<RepositorySnapshot>();
        CancellationToken token = default;
        fixture.GitProxy.Intercept = (method, args) =>
        {
            if (method.Name != nameof(IGitRepositoryService.GetSnapshotAsync) || (string)args[0]! != fixture.A) return null;
            token = (CancellationToken)args[1]!;
            return delay.Wait(); // Intentionally ignore cancellation to reproduce a late result.
        };
        var pending = (amend ? vm.AmendCommand : vm.CommitCommand).ExecuteAsync(null);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var repository = new Repository(fixture.A))
        {
            Assert.Equal("commit on A", repository.Head.Tip.MessageShort);
            Assert.Equal(amend ? 1 : 2, repository.Commits.Count());
        }
        if (close) vm.Dispose();
        else
        {
            Assert.True(await vm.OpenRepositoryAsync(fixture.B).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await vm.SelectCommitAsync(vm.History.First()));
        }
        Assert.True(token.IsCancellationRequested);
        var selected = vm.SelectedCommit;
        vm.CommitMessage = "new repository draft";
        var status = vm.StatusText; var history = vm.History.ToArray(); var tree = vm.FileTree.ToArray();
        delay.Complete(await fixture.Git.GetSnapshotAsync(fixture.A));
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        if (field == "message") Assert.Equal("new repository draft", vm.CommitMessage);
        if (field == "selection") Assert.Same(selected, vm.SelectedCommit);
        if (field == "status") Assert.Equal(status, vm.StatusText);
        Assert.Equal(history, vm.History); Assert.Equal(tree, vm.FileTree);
        Assert.Equal(otherHead, other.Head.Tip.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentCommitCompletionStillRefreshesAndClearsDraft(bool amend)
    {
        using var fixture = new Fixture(); using var vm = fixture.ViewModel();
        Assert.True(await vm.OpenRepositoryAsync(fixture.A));
        Assert.True((await fixture.Git.StageFilesAsync(fixture.A, ["a.txt"])).Success);
        Assert.True(await vm.SelectCommitAsync(vm.History.First()));
        vm.CommitMessage = "successful current commit";
        await (amend ? vm.AmendCommand : vm.CommitCommand).ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(vm.CommitMessage); Assert.Null(vm.SelectedCommit);
        Assert.False(vm.IsBrowsingHistoricalCommit); Assert.Equal(1, vm.SelectedRightTabIndex);
        Assert.Empty(vm.StagedChanges);
        Assert.Contains(vm.History, commit => commit.Message == "successful current commit");
        using var repository = new Repository(fixture.A);
        Assert.Equal("successful current commit", repository.Head.Tip.MessageShort);
        Assert.Equal(amend ? 1 : 2, repository.Commits.Count());
    }

    private sealed class Delayed<T>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<T> Wait() { Entered.TrySetResult(); return completion.Task; }
        internal void Complete(T result) => completion.SetResult(result);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        public string A { get; } public string B { get; }
        public LibGitRepositoryService Git { get; }
        public RecoveryService Recovery { get; }
        public ControlledProxy<IRecoveryService> RecoveryProxy { get; }
        private readonly IRecoveryService proxyRecovery;
        private readonly MemoryOperationLogStore log = new();
        public ControlledProxy<IGitRepositoryService> GitProxy { get; }
        public ControlledProxy<IDiffService> DiffProxy { get; }
        public ControlledProxy<IFileWorkspaceService> FilesProxy { get; }
        private readonly IGitRepositoryService proxyGit;
        private readonly IDiffService proxyDiff;
        private readonly IFileWorkspaceService proxyFiles;
        public Fixture()
        {
            A = CreateRepository("A"); B = CreateRepository("B");
            Recovery = new RecoveryService(new LocalDataPaths(Path.Combine(directory.Path, "data")));
            proxyRecovery = ControlledProxy<IRecoveryService>.Wrap(Recovery, out var recoveryProxy); RecoveryProxy = recoveryProxy;
            Git = new LibGitRepositoryService(Recovery, log);
            proxyGit = ControlledProxy<IGitRepositoryService>.Wrap(Git, out var git); GitProxy = git;
            proxyDiff = ControlledProxy<IDiffService>.Wrap(new LibGitDiffService(), out var diff); DiffProxy = diff;
            proxyFiles = ControlledProxy<IFileWorkspaceService>.Wrap(new FileWorkspaceService(), out var files); FilesProxy = files;
        }
        private string CreateRepository(string name)
        {
            var root = Path.Combine(directory.Path, name); Directory.CreateDirectory(root); Repository.Init(root);
            using var repository = new Repository(root); repository.Config.Set("core.autocrlf", false);
            File.WriteAllText(Path.Combine(root, "a.txt"), name + "-base"); File.WriteAllText(Path.Combine(root, "b.txt"), name + "-second");
            Commands.Stage(repository, new[] { "a.txt", "b.txt" });
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repository.Commit(name + "-base", signature, signature);
            var main = repository.Head.FriendlyName; var side = repository.CreateBranch("side"); Commands.Checkout(repository, side);
            File.WriteAllText(Path.Combine(root, "side.txt"), name + "-side"); Commands.Stage(repository, "side.txt"); repository.Commit(name + "-side", signature, signature);
            Commands.Checkout(repository, main); File.WriteAllText(Path.Combine(root, "a.txt"), name + "-dirty"); return root;
        }
        public MainWindowViewModel ViewModel() => new(proxyGit, proxyDiff, new NoOpRepositoryWatcherFactory(), proxyFiles,
            new WindowsShellNewFileService(), new MemorySettingsStore(), log, proxyRecovery, new MemoryCredentialVault());
        public void Dispose() => directory.Dispose();
    }
}

public class ControlledProxy<T> : DispatchProxy where T : class
{
    private T inner = null!;
    internal Func<MethodInfo, object?[], object?>? Intercept { get; set; }
    internal static T Wrap(T target, out ControlledProxy<T> control)
    {
        var proxy = Create<T, ControlledProxy<T>>(); control = (ControlledProxy<T>)(object)proxy;
        control.inner = target; return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        Intercept?.Invoke(method!, args ?? []) ?? method!.Invoke(inner, args);
}
