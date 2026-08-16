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
}
