using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class DiffAndRecoveryTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public async Task SelectedHunk_StagesWithoutStagingOtherHunks()
    {
        using var temporary = new TemporaryDirectory();
        var log = new MemoryOperationLogStore();
        var service = new LibGitRepositoryService(new RecoveryService(), log);
        var diff = new LibGitDiffService();
        var patch = new LibGitIndexPatchService(log);
        await service.InitializeAsync(temporary.Path, Identity);
        var path = System.IO.Path.Combine(temporary.Path, "many-lines.txt");
        var original = Enumerable.Range(1, 30).Select(number => $"line {number}").ToArray();
        await File.WriteAllLinesAsync(path, original);
        await service.StageFilesAsync(temporary.Path, ["many-lines.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        original[1] = "changed near start";
        original[24] = "changed near end";
        await File.WriteAllLinesAsync(path, original);
        var hunks = await diff.GetWorkingDiffAsync(temporary.Path, "many-lines.txt", false);
        Assert.Equal(2, hunks.Count);

        var result = await patch.StageHunksAsync(
            temporary.Path, "many-lines.txt", [hunks[0]]);
        Assert.True(result.Success, result.ErrorMessage);
        var staged = await diff.GetUnifiedDiffAsync(temporary.Path, "many-lines.txt", true);
        var unstaged = await diff.GetUnifiedDiffAsync(temporary.Path, "many-lines.txt", false);
        Assert.Contains("changed near start", staged);
        Assert.DoesNotContain("changed near end", staged);
        Assert.Contains("changed near end", unstaged);
    }

    [Fact]
    public async Task Discard_CreatesRestorableFileSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        await service.InitializeAsync(temporary.Path, Identity);
        var path = System.IO.Path.Combine(temporary.Path, "file.txt");
        await File.WriteAllTextAsync(path, "committed");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        await File.WriteAllTextAsync(path, "valuable uncommitted content");

        var discarded = await service.DiscardFilesAsync(temporary.Path, ["file.txt"]);
        Assert.True(discarded.Success, discarded.ErrorMessage);
        Assert.Equal("committed", await File.ReadAllTextAsync(path));
        var point = Assert.Single(
            await recovery.ListAsync(temporary.Path),
            item => item.Id == discarded.RecoveryPointId);
        var restored = await recovery.RestoreAsync(point);
        Assert.True(restored.Success, restored.ErrorMessage);
        Assert.Equal("valuable uncommitted content", await File.ReadAllTextAsync(path));
        File.Delete(point.ArchivePath);
    }
}
