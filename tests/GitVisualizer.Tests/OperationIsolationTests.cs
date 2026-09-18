using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class OperationIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitSuccessIsPreservedWhenLogFailsOrThrowsCancellation(bool cancelLog)
    {
        using var fixture = new Fixture();
        var log = new FailingLog(cancelLog);
        var git = new LibGitRepositoryService(fixture.Recovery, log);
        var result = await git.CreateBranchAsync(fixture.Path, "once");
        Assert.True(result.Success);
        Assert.Equal(GitOperationOutcome.Completed, result.Outcome);
        Assert.Equal(OperationLogStatus.Failed, result.LogStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("日志"));
        Assert.Equal(1, log.Attempts);
        Assert.True(Assert.Single(log.Entries).Success);
        using var repository = new Repository(fixture.Path);
        Assert.NotNull(repository.Branches["once"]);
        Assert.Equal("dirty\n", File.ReadAllText(fixture.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HunkSuccessIsPreservedWhenLogFailsOrThrowsCancellation(bool cancelLog)
    {
        using var fixture = new Fixture(); var log = new FailingLog(cancelLog);
        var patch = new LibGitIndexPatchService(log);
        var hunks = await new LibGitDiffService().GetWorkingDiffAsync(fixture.Path, "a.txt", false);
        var before = File.ReadAllBytes(fixture.FilePath);
        var result = await patch.StageHunksAsync(fixture.Path, "a.txt", hunks);
        Assert.True(result.Success);
        Assert.Equal(GitOperationOutcome.Completed, result.Outcome);
        Assert.Equal(OperationLogStatus.Failed, result.LogStatus);
        Assert.Equal(1, log.Attempts);
        using var repository = new Repository(fixture.Path);
        Assert.Equal("dirty\n", repository.Lookup<Blob>(repository.Index["a.txt"].Id)!.GetContentText());
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
    }

    [Fact]
    public async Task SameRepositoryLockCoversGitFilesHunksRecoveryAndCancellation()
    {
        using var fixture = new Fixture();
        var point = await fixture.Recovery.CreateAsync(fixture.Path, "saved");
        var files = new FileWorkspaceService();
        var document = await files.OpenTextAsync(fixture.FilePath);
        var hunks = await new LibGitDiffService().GetWorkingDiffAsync(fixture.Path, "a.txt", false);
        var before = File.ReadAllBytes(fixture.IndexPath);
        string head;
        using (var repository = new Repository(fixture.Path)) head = repository.Head.Tip!.Id.Sha;
        var gate = GitServiceSupport.LockFor(fixture.Path + System.IO.Path.DirectorySeparatorChar);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var save = files.SaveTextAsync(fixture.Path, document, "should not be saved", false, cancellation.Token);
            var stage = fixture.Git.StageFilesAsync(fixture.Path, new[] { "a.txt" }, cancellation.Token);
            var hunk = new LibGitIndexPatchService(new MemoryOperationLogStore()).StageHunksAsync(fixture.Path, "a.txt", hunks, cancellation.Token);
            var restore = fixture.Recovery.RestoreAsync(point, cancellation.Token);
            var backup = fixture.Recovery.CreateAsync(fixture.Path, "queued", cancellationToken: cancellation.Token);
            Assert.False(save.IsCompleted); Assert.False(stage.IsCompleted); Assert.False(hunk.IsCompleted);
            Assert.False(restore.IsCompleted); Assert.False(backup.IsCompleted);
            Assert.Equal(before, File.ReadAllBytes(fixture.IndexPath));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
            foreach (var result in new[] { await stage, await hunk, await restore })
            {
                Assert.False(result.Success);
                Assert.Equal(GitOperationOutcome.CanceledBeforeExecution, result.Outcome);
            }
            Assert.Equal("dirty\n", File.ReadAllText(fixture.FilePath));
            Assert.Equal(before, File.ReadAllBytes(fixture.IndexPath));
            using var repository = new Repository(fixture.Path);
            Assert.Equal(head, repository.Head.Tip!.Id.Sha);
            Assert.Single(repository.Refs, reference => reference.CanonicalName.StartsWith("refs/gitvisualizer/recovery/"));
        }
        finally { gate.Release(); }
        // The canceled waiters did not release someone else's lease or poison the shared lock.
        await files.SaveTextAsync(fixture.Path, document, "saved after cancellation\n", false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await fixture.Git.StageFilesAsync(fixture.Path, new[] { "a.txt" }).WaitAsync(TimeSpan.FromSeconds(5))).Success);
        var next = await fixture.Recovery.CreateAsync(fixture.Path, "next").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(next.ArchivePath));
        Assert.True((await fixture.Recovery.RestoreAsync(point).WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public void OutcomesDistinguishFailureCancellationAndPartialCompletion()
    {
        Assert.Equal(GitOperationOutcome.Failed, GitOperationResult.Fail("x", "x", new IOException()).Outcome);
        Assert.Equal(GitOperationOutcome.CanceledBeforeExecution, GitOperationResult.Fail("x", "x", new OperationCanceledException()).Outcome);
        Assert.Equal(GitOperationOutcome.PartiallyCompleted, GitOperationResult.Interrupted("x", "x", new OperationCanceledException()).Outcome);
        var partial = GitOperationResult.Fail("stage", "git add", new IOException()) with { WorktreeSaved = true };
        Assert.Equal(GitOperationOutcome.PartiallyCompleted, partial.Outcome);
    }

    private sealed class FailingLog(bool cancel) : IOperationLogStore
    {
        public int Attempts { get; private set; }
        public List<OperationLogEntry> Entries { get; } = new();
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(string? repositoryPath, int count, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OperationLogEntry>>([]);
        public Task AddAsync(OperationLogEntry entry, CancellationToken cancellationToken = default)
        {
            Attempts++; Entries.Add(entry);
            return Task.FromException(cancel ? new OperationCanceledException("log canceled") : new IOException("log disk full"));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory root = new();
        public string Path { get; }
        public string FilePath => System.IO.Path.Combine(Path, "a.txt");
        public string IndexPath => System.IO.Path.Combine(Path, ".git", "index");
        public RecoveryService Recovery { get; }
        public LibGitRepositoryService Git { get; }
        public Fixture()
        {
            Path = System.IO.Path.Combine(root.Path, "repo"); Directory.CreateDirectory(Path); Repository.Init(Path);
            using var repository = new Repository(Path); repository.Config.Set("core.autocrlf", false);
            File.WriteAllText(FilePath, "base\n"); Commands.Stage(repository, "a.txt");
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repository.Commit("base", signature, signature); File.WriteAllText(FilePath, "dirty\n");
            Recovery = new RecoveryService(new LocalDataPaths(System.IO.Path.Combine(root.Path, "data")));
            Git = new LibGitRepositoryService(Recovery, new MemoryOperationLogStore());
        }
        public void Dispose() => root.Dispose();
    }
}
