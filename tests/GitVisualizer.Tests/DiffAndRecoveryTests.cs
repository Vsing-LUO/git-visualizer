using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;

namespace GitVisualizer.Tests;

public sealed class DiffAndRecoveryTests
{
    private static readonly GitIdentity Identity = new("测试用户", "test@example.invalid");

    [Fact]
    public void RecoveryPoint_ConvertsUtcTimestampToCurrentLocalTime()
    {
        var createdAt = new DateTimeOffset(2026, 8, 24, 6, 30, 0, TimeSpan.Zero);
        var point = new RecoveryPoint(
            "id", "repository", "operation", "head", "reference", "archive",
            createdAt, 0, true);

        Assert.Equal(createdAt.ToLocalTime(), point.LocalCreatedAt);
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

    [Fact]
    public async Task Discard_RestoresWorkTreeFromIndexWithoutChangingStagedContent()
    {
        using var temporary = new TemporaryDirectory();
        var recovery = new RecoveryService();
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        var diff = new LibGitDiffService();
        await service.InitializeAsync(temporary.Path, Identity);
        using (var repository = new LibGit2Sharp.Repository(temporary.Path))
        {
            repository.Config.Set("core.autocrlf", true, LibGit2Sharp.ConfigurationLevel.Local);
        }
        var path = Path.Combine(temporary.Path, "file.txt");
        await File.WriteAllTextAsync(path, "base\n");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await service.CommitAsync(temporary.Path, "base", Identity);

        await File.WriteAllTextAsync(path, "staged version\n");
        await service.StageFilesAsync(temporary.Path, ["file.txt"]);
        await File.WriteAllTextAsync(path, "unstaged version\n");
        var untrackedPath = Path.Combine(temporary.Path, "untracked.txt");
        await File.WriteAllTextAsync(untrackedPath, "temporary\n");

        var discarded = await service.DiscardFilesAsync(
            temporary.Path,
            ["file.txt", "untracked.txt"]);

        Assert.True(discarded.Success, discarded.ErrorMessage);
        Assert.Equal("staged version\n", (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n"));
        Assert.Contains("\r\n", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(untrackedPath));
        Assert.Contains("staged version", await diff.GetUnifiedDiffAsync(
            temporary.Path, "file.txt", staged: true));
        Assert.Empty(await diff.GetWorkingDiffAsync(
            temporary.Path, "file.txt", staged: false));
        Assert.Contains(discarded.Warnings, warning => warning.Contains("暂存内容保持不变"));

        foreach (var point in await recovery.ListAsync(temporary.Path))
        {
            File.Delete(point.ArchivePath);
        }
    }

    [Fact]
    public async Task CommitComparison_FormatsDirectionChinesePathsAndEmptyResult()
    {
        using var temporary = new TemporaryDirectory();
        var service = new LibGitRepositoryService(
            new RecoveryService(),
            new MemoryOperationLogStore());
        var diff = new LibGitDiffService();
        await service.InitializeAsync(temporary.Path, Identity);
        const string path = "中文比较.txt";
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, path), "第一版\n");
        await service.StageFilesAsync(temporary.Path, [path]);
        await service.CommitAsync(temporary.Path, "base", Identity);
        var oldCommit = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, path), "第二版\n");
        await service.StageFilesAsync(temporary.Path, [path]);
        await service.CommitAsync(temporary.Path, "second", Identity);
        var newCommit = (await service.GetSnapshotAsync(temporary.Path)).Head.CommitId;

        var comparison = await diff.CompareCommitsAsync(
            temporary.Path, oldCommit, newCommit);
        var identical = await diff.CompareCommitsAsync(
            temporary.Path, newCommit, newCommit);
        var structured = await diff.CompareCommitsPresentationAsync(
            temporary.Path, oldCommit, newCommit);
        var structuredIdentical = await diff.CompareCommitsPresentationAsync(
            temporary.Path, newCommit, newCommit);

        Assert.Contains($"{oldCommit[..8]} → {newCommit[..8]}", comparison);
        Assert.Contains(path, comparison);
        Assert.Contains("第二版", comparison);
        Assert.DoesNotContain(@"\344\270", comparison);
        Assert.Contains("文件内容相同", identical);
        Assert.Contains(oldCommit[..8], structured.Title);
        Assert.Contains(newCommit[..8], structured.Title);
        Assert.Equal(path, Assert.Single(structured.Files).Path);
        Assert.False(structuredIdentical.HasFiles);
        Assert.Contains("没有发现", structuredIdentical.Summary);
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
    public void NaturalLanguageDiff_GroupsModificationAdditionAndContext()
    {
        const string patch = """
            diff --git a/test.txt b/test.txt
            index 432fe4c..0dd6609 100644
            --- a/test.txt
            +++ b/test.txt
            @@ -1,3 +1,4 @@
             unchanged
            -old value
            +new value
            +another value
             end

            """;

        var presentation = NaturalLanguageDiffParser.Parse(
            patch, "暂存区 → 工作区", "暂存区", "工作区");

        var file = Assert.Single(presentation.Files);
        Assert.Equal("test.txt", file.Path);
        Assert.Equal(DiffFileChangeKind.Modified, file.ChangeKind);
        Assert.Contains("1 个变化区域", presentation.Summary);
        var region = Assert.Single(file.Regions);
        Assert.Contains("修改前第 1–3 行", region.LocationText);
        var modification = Assert.Single(
            region.Blocks,
            block => block.Kind == DiffChangeBlockKind.Modified);
        Assert.Equal(2, modification.Rows.Count);
        Assert.Equal("old value", modification.Rows[0].OldLine?.Text);
        Assert.Equal("new value", modification.Rows[0].NewLine?.Text);
        Assert.Null(modification.Rows[1].OldLine);
        Assert.Equal("（无对应行）", modification.Rows[1].OldDisplayText);
        Assert.Equal("这一侧不存在该行", modification.Rows[1].OldAnnotation);
        Assert.Equal("another value", modification.Rows[1].NewLine?.Text);
        Assert.Contains(region.Blocks, block => block.Kind == DiffChangeBlockKind.Context);
        Assert.DoesNotContain("index 432fe4c", file.Summary);
        Assert.Contains("index 432fe4c", presentation.RawText);
    }

    [Fact]
    public void NaturalLanguageDiff_ExplainsFileStatesAndBinaryContent()
    {
        const string patch = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1 @@
            +new
            diff --git a/deleted.txt b/deleted.txt
            deleted file mode 100644
            index 2222222..0000000
            --- a/deleted.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            diff --git a/old.txt b/renamed.txt
            similarity index 100%
            rename from old.txt
            rename to renamed.txt
            diff --git a/image.bin b/image.bin
            index 3333333..4444444 100644
            Binary files a/image.bin and b/image.bin differ

            """;

        var presentation = NaturalLanguageDiffParser.Parse(
            patch, "旧提交 → 新提交", "旧提交", "新提交");

        Assert.Equal(4, presentation.Files.Count);
        Assert.Contains(presentation.Files, file => file.ChangeKind == DiffFileChangeKind.Added);
        Assert.Contains(presentation.Files, file => file.ChangeKind == DiffFileChangeKind.Deleted);
        var renamed = Assert.Single(
            presentation.Files,
            file => file.ChangeKind == DiffFileChangeKind.Renamed);
        Assert.Equal("old.txt → renamed.txt", renamed.DisplayPath);
        var binary = Assert.Single(presentation.Files, file => file.IsBinary);
        Assert.Contains("无法像文本一样逐行比较", binary.Summary);
        Assert.DoesNotContain("Binary files", binary.Summary);
    }

    [Fact]
    public void NaturalLanguageDiff_MarksWhitespaceAndCollapsesNewlineOnlyChange()
    {
        const string whitespacePatch = """
            diff --git a/test.txt b/test.txt
            --- a/test.txt
            +++ b/test.txt
            @@ -1 +1 @@
            -one value
            +one  value

            """;
        var whitespace = NaturalLanguageDiffParser.Parse(
            whitespacePatch, "暂存区 → 工作区", "暂存区", "工作区");
        var pair = Assert.Single(Assert.Single(
            Assert.Single(whitespace.Files).Regions).Blocks).Rows[0];
        Assert.True(pair.OldLine?.VisualizeWhitespace);
        Assert.True(pair.NewLine?.VisualizeWhitespace);
        Assert.Contains("·", pair.NewLine?.DisplayText);

        var blankLine = new DiffDisplayLine(3, string.Empty);
        var spacesOnly = new DiffDisplayLine(4, " \t ");
        Assert.Equal("（空行）", blankLine.DisplayText);
        Assert.Contains("·", spacesOnly.DisplayText);
        Assert.Contains("→", spacesOnly.DisplayText);
        Assert.Equal("此行只包含空格或 Tab", spacesOnly.Annotation);

        const string newlinePatch = """
            diff --git a/test.txt b/test.txt
            --- a/test.txt
            +++ b/test.txt
            @@ -1 +1 @@
            -same text
            \ No newline at end of file
            +same text

            """;
        var newline = NaturalLanguageDiffParser.Parse(
            newlinePatch, "暂存区 → 工作区", "暂存区", "工作区");
        var newlineFile = Assert.Single(newline.Files);
        Assert.Contains(newlineFile.Notices, notice => notice.Contains("补上了文件末尾"));
        Assert.Empty(Assert.Single(newlineFile.Regions).Blocks);
    }

    [Fact]
    public async Task StructuredWorkingDiff_PreservesSelectableHunkAndDirection()
    {
        using var temporary = new TemporaryDirectory();
        var repositoryService = new LibGitRepositoryService(
            new RecoveryService(),
            new MemoryOperationLogStore());
        var diff = new LibGitDiffService();
        await repositoryService.InitializeAsync(temporary.Path, Identity);
        var path = Path.Combine(temporary.Path, "file.txt");
        await File.WriteAllTextAsync(path, "before\n");
        await repositoryService.StageFilesAsync(temporary.Path, ["file.txt"]);
        await repositoryService.CommitAsync(temporary.Path, "base", Identity);
        await File.WriteAllTextAsync(path, "after\n");

        var presentation = await diff.GetWorkingDiffPresentationAsync(
            temporary.Path, "file.txt", staged: false);

        Assert.Equal("暂存区 → 工作区", presentation.Title);
        var region = Assert.Single(Assert.Single(presentation.Files).Regions);
        Assert.NotNull(region.SourceHunk);
        Assert.Contains(region.Blocks, block => block.Kind == DiffChangeBlockKind.Modified);
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
