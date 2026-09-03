using System.Diagnostics;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class M0RepositorySafetyTests
{
    private static readonly GitIdentity Identity = new("M0 测试用户", "m0@example.invalid");

    [Fact]
    public async Task RecoveryDeletionRemovesArchiveAndMatchingReference()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        await CreateCommittedRepositoryAsync(temporary.Path, service);
        var point = await recovery.CreateAsync(temporary.Path, "delete-lifecycle");

        using (var repository = new Repository(temporary.Path))
        {
            Assert.NotNull(repository.Refs[point.ReferenceName]);
        }
        Assert.True(File.Exists(point.ArchivePath));

        var deleted = await recovery.DeleteAsync(point);

        Assert.True(deleted.Success, deleted.ErrorMessage);
        Assert.False(File.Exists(point.ArchivePath));
        using var verification = new Repository(temporary.Path);
        Assert.Null(verification.Refs[point.ReferenceName]);
    }

    [Fact]
    public async Task RepositoryPruneKeepsLiveRecoveryAndRemovesOrphansAndOldSafetyRefs()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        await CreateCommittedRepositoryAsync(temporary.Path, service);
        var point = await recovery.CreateAsync(temporary.Path, "retained");
        using (var repository = new Repository(temporary.Path))
        {
            var target = repository.Head.Tip!.Id;
            repository.Refs.Add("refs/gitvisualizer/recovery/orphan", target, true);
            for (var index = 0; index < 55; index++)
            {
                var timestamp = DateTimeOffset.UtcNow.AddDays(index < 2 ? -31 : 0)
                    .AddMilliseconds(-index).ToString("yyyyMMddHHmmssfff");
                repository.Refs.Add(
                    $"refs/gitvisualizer/stash-backup/{timestamp}-{index:D2}", target, true);
            }
        }

        await recovery.PruneRepositoryReferencesAsync(temporary.Path);

        using (var repository = new Repository(temporary.Path))
        {
            Assert.NotNull(repository.Refs[point.ReferenceName]);
            Assert.Null(repository.Refs["refs/gitvisualizer/recovery/orphan"]);
            Assert.True(repository.Refs.Count(reference => reference.CanonicalName.StartsWith(
                "refs/gitvisualizer/stash-backup/", StringComparison.Ordinal)) <= 50);
            Assert.DoesNotContain(repository.Refs, reference =>
                reference.CanonicalName.Contains(DateTimeOffset.UtcNow.AddDays(-31).ToString("yyyyMMdd"), StringComparison.Ordinal));
        }

        Assert.True((await recovery.DeleteAsync(point)).Success);
    }

    [Fact]
    public async Task RealBisectStateIsVisibleAndContinueAbortDoNotMutateIt()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        await CreateCommittedRepositoryAsync(temporary.Path, service);
        for (var index = 1; index <= 2; index++)
        {
            await File.AppendAllTextAsync(Path.Combine(temporary.Path, "state.txt"), $"{index}\n");
            Assert.True((await service.StageFilesAsync(temporary.Path, ["state.txt"])).Success);
            Assert.True((await service.CommitAsync(temporary.Path, $"commit-{index}", Identity)).Success);
        }
        var history = await service.GetHistoryAsync(temporary.Path, 0, 10);
        await RunGitAsync(temporary.Path, "bisect", "start", history[0].Id, history[2].Id);
        try
        {
            var before = await service.GetSnapshotAsync(temporary.Path);
            string headBefore;
            string[] refsBefore;
            using (var repository = new Repository(temporary.Path))
            {
                headBefore = repository.Head.Tip!.Id.Sha;
                refsBefore = repository.Refs
                    .Where(reference => reference.CanonicalName.StartsWith("refs/gitvisualizer/", StringComparison.Ordinal))
                    .Select(reference => reference.CanonicalName).Order().ToArray();
            }

            var continued = await service.ContinueOperationAsync(temporary.Path, Identity);
            var aborted = await service.AbortOperationAsync(temporary.Path);

            Assert.Equal(RepositoryOperationState.Bisect, before.OperationState);
            Assert.False(continued.Success);
            Assert.False(aborted.Success);
            Assert.Contains("bisect", continued.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bisect", aborted.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            using var verification = new Repository(temporary.Path);
            Assert.Equal(CurrentOperation.Bisect, verification.Info.CurrentOperation);
            Assert.Equal(headBefore, verification.Head.Tip!.Id.Sha);
            Assert.Equal(refsBefore, verification.Refs
                .Where(reference => reference.CanonicalName.StartsWith("refs/gitvisualizer/", StringComparison.Ordinal))
                .Select(reference => reference.CanonicalName).Order().ToArray());
        }
        finally
        {
            await RunGitAsync(temporary.Path, "bisect", "reset");
        }
    }

    [Fact]
    public async Task InitializeWithoutIdentityDoesNotCreateFakeLocalConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var service = new LibGitRepositoryService(new RecoveryService(), new MemoryOperationLogStore());

        var result = await service.InitializeAsync(temporary.Path, null);

        Assert.True(result.Success, result.ErrorMessage);
        using var repository = new Repository(temporary.Path);
        Assert.Null(repository.Config.Get<string>("user.name", ConfigurationLevel.Local));
        Assert.Null(repository.Config.Get<string>("user.email", ConfigurationLevel.Local));
    }

    private static async Task CreateCommittedRepositoryAsync(
        string path, LibGitRepositoryService service)
    {
        Assert.True((await service.InitializeAsync(path, Identity)).Success);
        await File.WriteAllTextAsync(Path.Combine(path, "state.txt"), "base\n");
        Assert.True((await service.StageFilesAsync(path, ["state.txt"])).Success);
        Assert.True((await service.CommitAsync(path, "base", Identity)).Success);
    }

    private static async Task RunGitAsync(string repositoryPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动系统 Git。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output} {error}");
    }
}
