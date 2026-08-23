using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class DiffAndRecoveryTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

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

    [Fact]
    public async Task UnifiedDiff_ShowsReadableChinesePathsAndBinarySummary()
    {
        using var temporary = new TemporaryDirectory();
        var service = new LibGitRepositoryService(
            new RecoveryService(),
            new MemoryOperationLogStore());
        var diff = new LibGitDiffService();
        await service.InitializeAsync(temporary.Path, Identity);

        const string textPath = "中文路径.txt";
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, textPath), "第一版\n");
        await service.StageFilesAsync(temporary.Path, [textPath]);
        await service.CommitAsync(temporary.Path, "中文路径基线", Identity);
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, textPath), "第二版\n");

        var textDiff = await diff.GetUnifiedDiffAsync(temporary.Path, textPath, false);
        Assert.Contains("中文路径.txt", textDiff);
        Assert.DoesNotContain(@"\344\270", textDiff);
        Assert.Contains("第二版", textDiff);

        const string binaryPath = "新建 PPTX 测试文稿.pptx";
        await File.WriteAllBytesAsync(
            Path.Combine(temporary.Path, binaryPath),
            [80, 75, 3, 4, 0, 1, 2, 3]);

        var binaryDiff = await diff.GetUnifiedDiffAsync(temporary.Path, binaryPath, false);
        Assert.Contains("二进制文件", binaryDiff);
        Assert.Contains(binaryPath, binaryDiff);
        Assert.Contains("未暂存", binaryDiff);
        Assert.DoesNotContain("Binary files", binaryDiff);
        Assert.DoesNotContain(@"\346\226", binaryDiff);
    }

    [Fact]
    public async Task Restore_RecoversIndexAndWorkTreeOnNewBranchAndProtectsCurrentState()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var repositoryService = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        var diff = new LibGitDiffService();
        await repositoryService.InitializeAsync(temporary.Path, Identity);
        var fullPath = Path.Combine(temporary.Path, "state.txt");
        await File.WriteAllTextAsync(fullPath, "base\n");
        await repositoryService.StageFilesAsync(temporary.Path, ["state.txt"]);
        await repositoryService.CommitAsync(temporary.Path, "base", Identity);

        await File.WriteAllTextAsync(fullPath, "saved index\n");
        await repositoryService.StageFilesAsync(temporary.Path, ["state.txt"]);
        await File.WriteAllTextAsync(fullPath, "saved worktree\n");
        var point = await recovery.CreateAsync(temporary.Path, "test-full-restore");

        await File.WriteAllTextAsync(fullPath, "later index\n");
        await repositoryService.StageFilesAsync(temporary.Path, ["state.txt"]);
        await File.WriteAllTextAsync(fullPath, "later worktree\n");
        var restored = await recovery.RestoreAsync(point);

        Assert.True(restored.Success, restored.ErrorMessage);
        Assert.Equal("saved worktree\n", (await File.ReadAllTextAsync(fullPath)).Replace("\r\n", "\n"));
        Assert.Contains("saved index", await diff.GetUnifiedDiffAsync(temporary.Path, "state.txt", true));
        Assert.Contains("saved worktree", await diff.GetUnifiedDiffAsync(temporary.Path, "state.txt", false));
        using (var repository = new LibGit2Sharp.Repository(temporary.Path))
        {
            Assert.StartsWith("recovered/", repository.Head.FriendlyName, StringComparison.Ordinal);
        }
        var points = await recovery.ListAsync(temporary.Path);
        var protection = Assert.Single(points, item => item.Id == restored.RecoveryPointId);
        Assert.Equal("before-recovery-restore", protection.Operation);
        foreach (var archive in points.Select(item => item.ArchivePath).Distinct())
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task MultipleHunks_CanBeStagedAndUnstagedWithoutTouchingOtherHunks()
    {
        using var temporary = new TemporaryDirectory();
        var log = new MemoryOperationLogStore();
        var repositoryService = new LibGitRepositoryService(new RecoveryService(), log);
        var diff = new LibGitDiffService();
        var patches = new LibGitIndexPatchService(log);
        await repositoryService.InitializeAsync(temporary.Path, Identity);
        var path = Path.Combine(temporary.Path, "lines.txt");
        var original = Enumerable.Range(1, 45).Select(number => $"line {number}").ToArray();
        await File.WriteAllLinesAsync(path, original);
        await repositoryService.StageFilesAsync(temporary.Path, ["lines.txt"]);
        await repositoryService.CommitAsync(temporary.Path, "base", Identity);
        var changed = original.ToArray();
        changed[1] = "changed first";
        changed[20] = "changed middle";
        changed[40] = "changed last";
        await File.WriteAllLinesAsync(path, changed);

        var hunks = await diff.GetWorkingDiffAsync(temporary.Path, "lines.txt", staged: false);
        Assert.Equal(3, hunks.Count);
        var staged = await patches.StageHunksAsync(
            temporary.Path, "lines.txt", [hunks[0], hunks[2]]);
        Assert.True(staged.Success, staged.ErrorMessage);

        var stagedText = await diff.GetUnifiedDiffAsync(temporary.Path, "lines.txt", staged: true);
        var unstagedText = await diff.GetUnifiedDiffAsync(temporary.Path, "lines.txt", staged: false);
        Assert.Contains("changed first", stagedText);
        Assert.Contains("changed last", stagedText);
        Assert.DoesNotContain("changed middle", stagedText);
        Assert.Contains("changed middle", unstagedText);
        Assert.DoesNotContain("changed first", unstagedText);

        var stagedHunks = await diff.GetWorkingDiffAsync(temporary.Path, "lines.txt", staged: true);
        var unstaged = await patches.UnstageHunksAsync(temporary.Path, "lines.txt", stagedHunks);
        Assert.True(unstaged.Success, unstaged.ErrorMessage);
        Assert.Empty(await diff.GetWorkingDiffAsync(temporary.Path, "lines.txt", staged: true));
        Assert.Equal(changed, await File.ReadAllLinesAsync(path));
    }

    [Fact]
    public async Task HunkStaging_RejectsSnapshotAfterFileChanges()
    {
        using var temporary = new TemporaryDirectory();
        var log = new MemoryOperationLogStore();
        var repositoryService = new LibGitRepositoryService(new RecoveryService(), log);
        var diff = new LibGitDiffService();
        var patches = new LibGitIndexPatchService(log);
        await repositoryService.InitializeAsync(temporary.Path, Identity);
        var path = Path.Combine(temporary.Path, "file.txt");
        await File.WriteAllTextAsync(path, "base\n");
        await repositoryService.StageFilesAsync(temporary.Path, ["file.txt"]);
        await repositoryService.CommitAsync(temporary.Path, "base", Identity);
        await File.WriteAllTextAsync(path, "first change\n");
        var hunk = Assert.Single(await diff.GetWorkingDiffAsync(temporary.Path, "file.txt", false));
        await File.WriteAllTextAsync(path, "later change\n");

        var result = await patches.StageHunksAsync(temporary.Path, "file.txt", [hunk]);

        Assert.False(result.Success);
        Assert.Contains("刷新", result.ErrorMessage);
        Assert.Empty(await diff.GetWorkingDiffAsync(temporary.Path, "file.txt", true));
    }
}
