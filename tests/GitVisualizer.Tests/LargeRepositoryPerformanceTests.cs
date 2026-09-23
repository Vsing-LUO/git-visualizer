// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using System.Diagnostics;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;
using GitVisualizer.Infrastructure.FileSystem;

namespace GitVisualizer.Tests;

public sealed class LargeRepositoryPerformanceTests
{
    [Fact]
    public async Task OneGiBFileDoesNotEnterEditorOrAllocateItsContents()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "large.bin");
        using (var stream = File.Create(path)) stream.SetLength(1L << 30);
        await TextFileStorage.OpenAsync(path); // warm up
        var before = GC.GetTotalAllocatedBytes(true);
        var document = await TextFileStorage.OpenAsync(path);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(1L << 30, document.Size);
        Assert.True(document.IsReadOnly);
        Assert.Empty(document.Text);
        Assert.Null(document.ContentBytes);
        Assert.Null(document.OriginalByteDigest);
        Assert.True(allocated < 32L * 1024 * 1024, $"Allocated {allocated} bytes");
    }

    [Fact]
    public async Task LazyDirectoryHasNoDepthOrFiveHundredEntryLimitAndRetainsNodes()
    {
        using var directory = new TemporaryDirectory();
        var deep = directory.Path;
        for (var i = 0; i < 10; i++) deep = Directory.CreateDirectory(Path.Combine(deep, "deep")).FullName;
        for (var i = 0; i < 601; i++) File.WriteAllText(Path.Combine(deep, $"{i:000}.txt"), "ok");
        var item = FileTreeItem.Create(directory.Path, 100);
        Assert.True(Assert.Single(item.Children).IsPlaceholder);
        for (var i = 0; i < 10; i++)
        {
            await item.LoadChildrenAsync();
            item = Assert.Single(item.Children);
        }
        await item.LoadChildrenAsync();
        Assert.Equal(601, item.Children.Count);
        var original = item.Children[300];
        await item.LoadChildrenAsync(refresh: true);
        Assert.Same(original, item.Children[300]);
    }

    [Fact]
    public async Task DiffBudgetsBlockHunksButAllowWholeFileStaging()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        var path = Path.Combine(directory.Path, "large.txt");
        File.WriteAllText(path, "base\n");
        using (var repo = new Repository(directory.Path))
        {
            repo.Config.Set("core.autocrlf", false);
            Commands.Stage(repo, "large.txt");
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repo.Commit("base", signature, signature);
        }
        using (var stream = File.OpenWrite(path)) stream.SetLength(9L * 1024 * 1024);
        var diff = new LibGitDiffService();
        var presentation = await diff.GetWorkingDiffPresentationAsync(directory.Path, "large.txt", false);
        Assert.Contains("预算", presentation.Summary);
        Assert.Empty(await diff.GetWorkingDiffAsync(directory.Path, "large.txt", false));
        var git = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        Assert.True((await git.StageFilesAsync(directory.Path, ["large.txt"])).Success);
        Assert.Contains("预算", (await diff.GetWorkingDiffPresentationAsync(directory.Path, "large.txt", true)).Summary);
        File.WriteAllLines(path, Enumerable.Range(0, 20010).Select(i => "line " + i));
        // Compare against a small index to exercise the line budget independently of byte size.
        using (var repo = new Repository(directory.Path)) Commands.Unstage(repo, "large.txt");
        Assert.Contains("预算", (await diff.GetWorkingDiffPresentationAsync(directory.Path, "large.txt", false)).Summary);
    }

    [Fact]
    public async Task HistoryLookaheadHasNoDuplicatesOrMissingCommitsAndResetsOnNewHead()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        string[] expected;
        using (var repo = new Repository(directory.Path))
        {
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            for (int i = 0; i < 405; i++) repo.Commit("commit " + i, signature, signature, new CommitOptions { AllowEmptyCommit = true });
            expected = repo.Commits.Select(x => x.Id.Sha).ToArray();
        }
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        try
        {
            var singleTraversal = (await service.GetHistoryAsync(directory.Path, 0, 1000)).Select(x => x.Id).ToArray();
            Assert.Equal(expected.Order(), singleTraversal.Order());
            var actual = new List<string>();
            for (int skip = 0; skip < expected.Length; skip += 200)
                actual.AddRange((await service.GetHistoryAsync(directory.Path, skip, 201)).Take(200).Select(x => x.Id));
            Assert.Equal(singleTraversal, actual);
            Assert.Equal(expected.Length, actual.Distinct().Count());
            using (var repo = new Repository(directory.Path))
            {
                var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
                repo.Commit("new head", signature, signature, new CommitOptions { AllowEmptyCommit = true });
            }
            service.ResetHistorySession();
            Assert.Equal("new head", (await service.GetHistoryAsync(directory.Path, 0, 201))[0].Message);
        }
        finally { service.ResetHistorySession(); }
    }

    [Fact]
    public async Task HistoricalLargeBlobIsBoundedAndExportsExactBytes()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        var path = Path.Combine(directory.Path, "large.bin");
        using (var stream = File.Create(path)) { stream.SetLength(6L * 1024 * 1024); stream.WriteByte(42); }
        string id;
        using (var repo = new Repository(directory.Path))
        {
            Commands.Stage(repo, "large.bin");
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            id = repo.Commit("large", signature, signature).Id.Sha;
        }
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        var document = await service.OpenCommitFileAsync(directory.Path, id, "large.bin");
        Assert.Empty(document.Text);
        Assert.Null(document.ContentBytes);
        Assert.True(document.IsReadOnly);
        var export = Path.Combine(directory.Path, "export.bin");
        await service.ExportCommitFileAsync(directory.Path, id, "large.bin", export, default);
        using var input = File.OpenRead(path);
        using var output = File.OpenRead(export);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(input), System.Security.Cryptography.SHA256.HashData(output));
    }

    [Fact]
    public async Task WatcherClassifiesExternalIndexAndHeadChangesWithinTwoSeconds()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        using var watcher = new RepositoryWatcher(directory.Path);
        var observed = new TaskCompletionSource<RepositoryChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RepositoryChanged += (_, args) =>
        {
            if (args is RepositoryChangedEventArgs changed && (changed.Changes & RepositoryChangeKind.Index) != 0)
                observed.TrySetResult(changed.Changes);
        };
        watcher.Start();
        File.WriteAllText(Path.Combine(directory.Path, "new.txt"), "new");
        using (var repo = new Repository(directory.Path)) Commands.Stage(repo, "new.txt");
        var changes = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(changes.HasFlag(RepositoryChangeKind.Index));
    }

    [Fact]
    public async Task RefreshStormHasOneExecutionAndOneMergedPendingTask()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        var log = new MemoryOperationLogStore();
        var recovery = new RecoveryService();
        var git = new LibGitRepositoryService(recovery, log);
        var proxy = ControlledProxy<IGitRepositoryService>.Wrap(git, out var control);
        using var vm = new MainWindowViewModel(proxy, new LibGitDiffService(), new NoOpRepositoryWatcherFactory(),
            new FileWorkspaceService(), new WindowsShellNewFileService(), new MemorySettingsStore(), log, recovery,
            new MemoryCredentialVault());
        Assert.True(await vm.OpenRepositoryAsync(directory.Path));
        var snapshot = await git.GetSnapshotAsync(directory.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, active = 0, maximum = 0;
        async Task<RepositorySnapshot> Read()
        {
            var number = Interlocked.Increment(ref calls);
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                if (number == 1) { entered.SetResult(); await release.Task; }
                return snapshot;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        control.Intercept = (method, args) => method.Name == nameof(IGitRepositoryService.GetSnapshotAsync) ? Read() : null;
        var first = vm.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 1000; i++) Assert.Same(first, vm.RefreshAsync());
        Assert.Equal(1, calls);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task FocusRevalidationCompensatesForLostEventsAndUnchangedChecksStayQuiet()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        using var watcher = new RepositoryWatcher(directory.Path);
        var first = new TaskCompletionSource<RepositoryChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<RepositoryChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        watcher.RepositoryChanged += (_, args) =>
        {
            var changes = (args as RepositoryChangedEventArgs)?.Changes ?? RepositoryChangeKind.None;
            if (Interlocked.Increment(ref notifications) == 1) first.TrySetResult(changes);
            else recovered.TrySetResult(changes);
        };
        watcher.Start();
        watcher.Revalidate();
        Assert.Equal(RepositoryChangeKind.All, await first.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        watcher.Revalidate();
        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref notifications));
        // Deliberately lose the native notifications, then rely only on focus metadata.
        watcher.Stop();
        using (var repo = new Repository(directory.Path))
        {
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repo.Commit("external", signature, signature, new CommitOptions { AllowEmptyCommit = true });
        }
        watcher.Start();
        watcher.Revalidate();
        Assert.Equal(RepositoryChangeKind.All, await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref notifications));
    }

    [Fact]
    public async Task LinkedWorktreeWatcherUsesPrivateIndexAndCommonReferences()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "main");
        var linked = Path.Combine(directory.Path, "linked");
        Directory.CreateDirectory(root);
        Repository.Init(root);
        using (var repo = new Repository(root))
        {
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repo.Commit("base", signature, signature, new CommitOptions { AllowEmptyCommit = true });
        }
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-C", root, "worktree", "add", "-b", "linked", linked }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
        }
        using var watcher = new RepositoryWatcher(linked);
        var index = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reference = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RepositoryChanged += (_, args) =>
        {
            if (args is not RepositoryChangedEventArgs changed) return;
            if (changed.Changes.HasFlag(RepositoryChangeKind.Index)) index.TrySetResult();
            if (changed.Changes.HasFlag(RepositoryChangeKind.References)) reference.TrySetResult();
        };
        watcher.Start();
        File.WriteAllText(Path.Combine(linked, "linked.txt"), "linked");
        using (var repo = new Repository(linked)) Commands.Stage(repo, "linked.txt");
        await index.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (var repo = new Repository(root)) repo.CreateBranch("external-branch");
        await reference.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalCommitAndCheckoutNotifyAndProduceCurrentSnapshot(bool checkout)
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
        using (var repo = new Repository(directory.Path))
        {
            repo.Commit("base", signature, signature, new CommitOptions { AllowEmptyCommit = true });
            repo.CreateBranch("other");
        }
        using var watcher = new RepositoryWatcher(directory.Path);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RepositoryChanged += (_, args) =>
        {
            if (args is RepositoryChangedEventArgs changed && changed.Changes.HasFlag(RepositoryChangeKind.References))
                observed.TrySetResult();
        };
        watcher.Start();
        string expectedId;
        using (var external = new Repository(directory.Path))
        {
            if (checkout) Commands.Checkout(external, "other");
            else external.Commit("external", signature, signature, new CommitOptions { AllowEmptyCommit = true });
            expectedId = external.Head.Tip.Id.Sha;
        }
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        var snapshot = await service.GetSnapshotAsync(directory.Path);
        Assert.Equal(expectedId, snapshot.Head.CommitId);
        if (checkout) Assert.Equal("other", Assert.Single(snapshot.Branches, branch => branch.IsCurrent).FriendlyName);
    }

    [Fact]
    public async Task ConflictListContainsOnlyMetadataAndSelectedVersionKeepsSafeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        Repository.Init(directory.Path);
        using (var repo = new Repository(directory.Path))
        {
            repo.Config.Set("core.autocrlf", false);
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            void CommitFiles(string text)
            {
                foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(directory.Path, name), text);
                Commands.Stage(repo, new[] { "a.txt", "b.txt", "c.txt" });
                repo.Commit(text, signature, signature);
            }
            CommitFiles("base\n");
            var main = repo.Head.FriendlyName;
            var side = repo.CreateBranch("side");
            Commands.Checkout(repo, side); CommitFiles("side\n");
            Commands.Checkout(repo, main); CommitFiles("main\n");
            repo.Merge(side, signature, new MergeOptions());
        }
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());
        var metadata = await service.GetConflictMetadataAsync(directory.Path, default);
        Assert.Equal(3, metadata.Count);
        Assert.All(metadata, file =>
        {
            Assert.False(file.IsLoaded);
            Assert.Null(file.OriginalDocument);
            Assert.Empty(file.BaseText); Assert.Empty(file.OursText); Assert.Empty(file.TheirsText); Assert.Empty(file.ResultText);
        });
        var selected = await service.GetConflictAsync(directory.Path, "b.txt", default);
        Assert.True(selected.IsLoaded);
        Assert.Equal("base\n", selected.BaseText);
        Assert.Equal("main\n", selected.OursText);
        Assert.Equal("side\n", selected.TheirsText);
        Assert.NotNull(selected.OriginalDocument?.OriginalByteDigest);
        Assert.NotNull(selected.OriginalDocument?.ConflictIndexDigest);
        Assert.All(metadata, file => Assert.False(file.IsLoaded));
    }
}
