using System.Security.Cryptography;
using System.IO.Compression;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class ConflictRecoverySafetyTests
{
    [Theory]
    [InlineData("index-race")]
    [InlineData("stage-failure")]
    [InlineData("backup-failure")]
    public async Task ConflictSaveProtectsIndexAndDistinguishesPartialCompletion(string fault)
    {
        using var dir = new TemporaryDirectory();
        var root = Path.Combine(dir.Path, "repo");
        Directory.CreateDirectory(root);
        Repository.Init(root);
        var file = Path.Combine(root, "a.txt");
        var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
        using (var repository = new Repository(root))
        {
            repository.Config.Set("core.autocrlf", false);
            File.WriteAllText(file, "base\n"); Commands.Stage(repository, "a.txt"); repository.Commit("base", signature, signature);
            var main = repository.Head.FriendlyName;
            var side = repository.CreateBranch("side");
            Commands.Checkout(repository, side);
            File.WriteAllText(file, "side\n"); Commands.Stage(repository, "a.txt"); repository.Commit("side", signature, signature);
            Commands.Checkout(repository, main);
            File.WriteAllText(file, "main\n"); Commands.Stage(repository, "a.txt"); repository.Commit("main", signature, signature);
            repository.Merge(repository.Branches["side"], signature, new MergeOptions());
        }
        var recovery = new RecoveryService(new LocalDataPaths(Path.Combine(dir.Path, "data")));
        var service = new LibGitRepositoryService(recovery, new MemoryOperationLogStore());
        var conflict = Assert.Single(await service.GetConflictsAsync(root));
        var originalFile = File.ReadAllBytes(file);
        var indexPath = Path.Combine(root, ".git", "index");
        if (fault == "index-race")
        {
            File.WriteAllText(Path.Combine(root, "unrelated.txt"), "unrelated");
            using var repository = new Repository(root);
            Commands.Stage(repository, "unrelated.txt");
        }
        if (fault == "stage-failure") service.ConflictStageCheckpoint = () => throw new IOException("injected stage failure");
        if (fault == "backup-failure") recovery.Checkpoint = (stage, _) => { if (stage == "backup-write") throw new IOException("injected disk full"); };
        var indexBefore = File.ReadAllBytes(indexPath);
        string headBefore;
        using (var repository = new Repository(root)) headBefore = repository.Head.Tip!.Id.Sha;
        var result = await service.ResolveConflictAsync(root, "a.txt", "resolved\n", originalDocument: conflict.OriginalDocument);
        Assert.False(result.Success);
        Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
        using (var repository = new Repository(root))
        {
            Assert.Equal(headBefore, repository.Head.Tip!.Id.Sha);
            Assert.Single(repository.Index.Conflicts);
        }
        if (fault == "stage-failure")
        {
            Assert.True(result.WorktreeSaved);
            Assert.Equal("文件已保存、暂存失败", result.Summary);
            Assert.Equal("stage-failed", result.ExecutionStage);
            Assert.Equal("resolved\n", File.ReadAllText(file).Replace("\r\n", "\n"));
            var point = Assert.Single(await recovery.ListAsync(root), item => item.Id == result.RecoveryPointId);
            using var zip = ZipFile.OpenRead(point.ArchivePath);
            using var input = zip.GetEntry("files/a.txt")!.Open();
            using var memory = new MemoryStream(); input.CopyTo(memory);
            Assert.Equal(originalFile, memory.ToArray());
            using var repository = new Repository(root);
            Assert.NotNull(repository.Refs[point.ReferenceName]);
        }
        else
        {
            Assert.False(result.WorktreeSaved);
            Assert.Equal(originalFile, File.ReadAllBytes(file));
            if (fault == "backup-failure") Assert.Null(result.RecoveryPointId);
            else Assert.Contains("索引", result.ErrorMessage);
        }
    }
}
