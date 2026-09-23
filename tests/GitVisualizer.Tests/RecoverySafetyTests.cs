// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Recovery;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

public sealed class RecoverySafetyTests
{
    [Theory]
    [InlineData("truncated")]
    [InlineData("digest")]
    [InlineData("missing-index")]
    [InlineData("invalid-index")]
    [InlineData("outside")]
    [InlineData("git-path")]
    [InlineData("extra-entry")]
    [InlineData("duplicate")]
    [InlineData("missing-commit")]
    [InlineData("missing-reference")]
    [InlineData("legacy-crc")]
    public async Task DamagedArchivesAreRejectedBeforeAnyRepositoryMutation(string fault)
    {
        using var fixture = new Fixture();
        var point = await fixture.SavePointAsync();
        if (fault == "truncated")
        {
            var bytes = await File.ReadAllBytesAsync(point.ArchivePath);
            await File.WriteAllBytesAsync(point.ArchivePath, bytes[..(bytes.Length / 2)]);
        }
        else if (fault == "missing-reference")
        {
            using var repository = new Repository(fixture.RepositoryPath);
            repository.Refs.Remove(point.ReferenceName);
        }
        else
        {
            using (var zip = ZipFile.Open(point.ArchivePath, ZipArchiveMode.Update))
            {
                var manifest = ReadManifest(zip);
                if (fault == "digest") ReplaceEntry(zip, "files/a.txt", Encoding.UTF8.GetBytes("tampered"));
                if (fault == "missing-index") zip.GetEntry("git-index")!.Delete();
                if (fault == "invalid-index")
                {
                    var corrupt = Encoding.UTF8.GetBytes("invalid index");
                    ReplaceEntry(zip, "git-index", corrupt);
                    UpdateDigest(manifest, "git-index", corrupt);
                }
                if (fault == "outside" || fault == "git-path")
                {
                    string path = fault == "outside" ? "../outside.txt" : ".git/config";
                    manifest["affectedPaths"]!.AsArray().Add(path);
                    ReplaceEntry(zip, "files/" + path, Encoding.UTF8.GetBytes("escape"));
                }
                if (fault == "extra-entry") ReplaceEntry(zip, "unknown", new byte[] { 1 });
                if (fault == "duplicate") zip.CreateEntry("files/a.txt");
                if (fault == "missing-commit")
                {
                    manifest["headId"] = new string('0', 40);
                    point = point with { HeadId = new string('0', 40) };
                }
                if (fault == "legacy-crc") { manifest.Remove("version"); manifest.Remove("entries"); }
                ReplaceEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            }
            if (fault == "legacy-crc")
            {
                var bytes = await File.ReadAllBytesAsync(point.ArchivePath);
                // Corrupt a central-directory CRC while leaving ZIP structure and decompression intact.
                for (int i = 0; i < bytes.Length - 20; i++)
                    if (bytes.AsSpan(i, 4).SequenceEqual(new byte[] { 0x50, 0x4b, 0x01, 0x02 }))
                    { bytes[i + 16] ^= 0xff; break; }
                await File.WriteAllBytesAsync(point.ArchivePath, bytes);
            }
        }
        var before = fixture.State();
        var result = await fixture.Recovery.RestoreAsync(point);
        Assert.False(result.Success);
        Assert.Equal("preflight", result.ExecutionStage);
        Assert.Null(result.RecoveryPointId);
        Assert.Equal(before, fixture.State());
        Assert.True(File.Exists(point.ArchivePath));
        Assert.False(File.Exists(Path.Combine(fixture.Root.Path, "outside.txt")));
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RecoveryDirectory, ".restore-*"));
    }

    [Fact]
    public async Task LegacyArchiveIsFullyValidatedAndRestoresIndexAndHead()
    {
        using var fixture = new Fixture();
        var point = await fixture.SavePointAsync();
        using (var zip = ZipFile.Open(point.ArchivePath, ZipArchiveMode.Update))
        {
            var manifest = ReadManifest(zip);
            manifest.Remove("version"); manifest.Remove("entries"); manifest.Remove("headState");
            ReplaceEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        var savedIndex = ReadEntry(point.ArchivePath, "git-index");
        var result = await fixture.Recovery.RestoreAsync(point);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("saved a", File.ReadAllText(fixture.File("a.txt")));
        Assert.Equal(savedIndex, File.ReadAllBytes(fixture.IndexPath));
        using var repository = new Repository(fixture.RepositoryPath);
        Assert.Equal(point.HeadId, repository.Head.Tip!.Id.Sha);
        Assert.StartsWith("recovered/", repository.Head.FriendlyName);
        Assert.Equal(point.HeadId, repository.Refs[point.ReferenceName].ResolveToDirectReference().TargetIdentifier);
        var safety = Assert.Single(await fixture.Recovery.ListAsync(fixture.RepositoryPath), item => item.Id == result.RecoveryPointId);
        Assert.True(File.Exists(safety.ArchivePath + ".pin"));
        Assert.Equal("completed", JsonNode.Parse(File.ReadAllText(result.ExecutionRecordPath!))!["stage"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("new-file")]
    [InlineData("disk-full")]
    [InlineData("cancel")]
    [InlineData("quota")]
    [InlineData("actual-quota")]
    [InlineData("space")]
    [InlineData("missing-index")]
    public async Task RequiredBackupFailureBlocksDestructiveOperation(string fault)
    {
        using var fixture = new Fixture();
        var valuable = fault == "actual-quota" ? new string('x', 200000) : "valuable draft";
        File.WriteAllText(fixture.File("a.txt"), valuable);
        using var cancellation = new CancellationTokenSource();
        if (fault == "quota") fixture.Recovery.ByteLimit = 128;
        if (fault == "space") fixture.Recovery.AvailableDiskBytes = _ => 0;
        if (fault == "missing-index") File.Delete(fixture.IndexPath);
        bool injected = false;
        fixture.Recovery.Checkpoint = (stage, path) =>
        {
            if (injected) return;
            if (stage == "backup-write" && fault == "actual-quota") { fixture.Recovery.ByteLimit = 81921; injected = true; }
            if (stage == "backup-after-file" && path.EndsWith("a.txt"))
            {
                injected = true;
                if (fault == "mutation") File.WriteAllText(path, "external during backup");
                if (fault == "new-file") File.WriteAllText(fixture.File("new.txt"), "new file");
                if (fault == "cancel") cancellation.Cancel();
            }
            if (stage == "backup-write" && fault == "disk-full") { injected = true; throw new IOException("injected disk full"); }
        };
        var beforeHead = fixture.Head();
        var beforeIndex = File.Exists(fixture.IndexPath) ? File.ReadAllBytes(fixture.IndexPath) : null;
        var git = new LibGitRepositoryService(fixture.Recovery, new MemoryOperationLogStore());
        var result = await git.DiscardFilesAsync(fixture.RepositoryPath, ["a.txt"], cancellation.Token);
        Assert.False(result.Success);
        Assert.Null(result.RecoveryPointId);
        Assert.Equal(fault == "mutation" ? "external during backup" : valuable, File.ReadAllText(fixture.File("a.txt")));
        Assert.Equal(beforeHead, fixture.Head());
        Assert.Equal(beforeIndex, File.Exists(fixture.IndexPath) ? File.ReadAllBytes(fixture.IndexPath) : null);
        Assert.Empty(fixture.References());
        Assert.Empty(Directory.GetFiles(fixture.Paths.RecoveryDirectory, "*.zip"));
        Assert.Empty(Directory.GetFiles(fixture.Paths.RecoveryDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("occupied")]
    [InlineData("cancel-preflight")]
    [InlineData("missing-current-index")]
    [InlineData("safety-failure")]
    public async Task PreflightFailureDoesNotCheckoutOrCreateSafetyReference(string fault)
    {
        using var fixture = new Fixture();
        var point = await fixture.SavePointAsync();
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancel-preflight") cancellation.Cancel();
        if (fault == "missing-current-index") File.Delete(fixture.IndexPath);
        if (fault == "safety-failure") fixture.Recovery.Checkpoint = (stage, _) => { if (stage == "backup-write") throw new IOException("safety backup disk full"); };
        var before = fixture.State();
        FileStream? occupied = fault == "occupied" ? new FileStream(fixture.File("a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        GitOperationResult result;
        try { result = await fixture.Recovery.RestoreAsync(point, cancellation.Token); }
        finally { occupied?.Dispose(); }
        Assert.False(result.Success);
        Assert.Null(result.RecoveryPointId);
        Assert.Equal(before, fixture.State());
    }

    [Theory]
    [InlineData("worktree-failure")]
    [InlineData("worktree-cancel")]
    [InlineData("index-failure")]
    public async Task PartialRestoreRecordsStageAndKeepsSafetyArchiveAndReferences(string fault)
    {
        using var fixture = new Fixture();
        var point = await fixture.SavePointAsync();
        var beforeHead = fixture.Head();
        var beforeIndex = File.ReadAllBytes(fixture.IndexPath);
        using var cancellation = new CancellationTokenSource();
        fixture.Recovery.Checkpoint = (stage, path) =>
        {
            if (stage == "restore-copy" && path.EndsWith("b.txt") && fault != "index-failure")
            {
                if (fault == "worktree-cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw new IOException("injected mid-restore write failure");
            }
            if (stage == "index" && fault == "index-failure") throw new IOException("injected index replacement failure");
        };
        var result = await fixture.Recovery.RestoreAsync(point, cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal(fault == "index-failure" ? "index" : "worktree", result.ExecutionStage);
        Assert.Contains("部分", result.Summary);
        Assert.NotNull(result.RecoveryPointId);
        Assert.Equal("saved a", File.ReadAllText(fixture.File("a.txt")));
        Assert.Equal(fault == "index-failure" ? "saved b" : "base b", File.ReadAllText(fixture.File("b.txt")));
        using var repository = new Repository(fixture.RepositoryPath);
        Assert.Equal(point.HeadId, repository.Head.Tip!.Id.Sha);
        Assert.StartsWith("recovered/", repository.Head.FriendlyName);
        Assert.NotEqual(beforeHead.Split('\n')[1], repository.Head.Tip.Id.Sha);
        Assert.Equal(((Blob)repository.Head.Tip.Tree["a.txt"].Target).Id, repository.Index["a.txt"].Id);
        Assert.Equal(((Blob)repository.Head.Tip.Tree["b.txt"].Target).Id, repository.Index["b.txt"].Id);
        Assert.Equal(2, fixture.References().Length);
        var safety = Assert.Single(await fixture.Recovery.ListAsync(fixture.RepositoryPath), item => item.Id == result.RecoveryPointId);
        Assert.Equal("later a", Encoding.UTF8.GetString(ReadEntry(safety.ArchivePath, "files/a.txt")));
        Assert.Equal(beforeIndex, ReadEntry(safety.ArchivePath, "git-index"));
        Assert.Equal(beforeHead.Split('\n')[1], safety.HeadId);
        Assert.True(File.Exists(safety.ArchivePath + ".pin"));
        var journal = JsonNode.Parse(File.ReadAllText(result.ExecutionRecordPath!))!;
        Assert.True(journal["repositoryMayHaveChanged"]!.GetValue<bool>());
        Assert.Equal(safety.Id, journal["safetyPointId"]!.GetValue<string>());
        Assert.EndsWith("-failed", journal["stage"]!.GetValue<string>());
        File.SetLastWriteTimeUtc(safety.ArchivePath, DateTime.UtcNow.AddDays(-60));
        await fixture.Recovery.PruneAsync();
        Assert.True(File.Exists(safety.ArchivePath));
        Assert.NotNull(repository.Refs[safety.ReferenceName]);
    }

    [Fact]
    public async Task CorruptManifestDoesNotCauseRecoveryReferencePruning()
    {
        using var fixture = new Fixture();
        var point = await fixture.SavePointAsync();
        using (var zip = ZipFile.Open(point.ArchivePath, ZipArchiveMode.Update)) ReplaceEntry(zip, "manifest.json", new byte[] { 0xff });
        await fixture.Recovery.PruneRepositoryReferencesAsync(fixture.RepositoryPath);
        Assert.Contains(point.ReferenceName, fixture.References());
    }

    private static JsonObject ReadManifest(ZipArchive zip)
    {
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonNode.Parse(stream)!.AsObject();
    }
    private static void ReplaceEntry(ZipArchive zip, string name, byte[] bytes)
    {
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }
    private static void UpdateDigest(JsonObject manifest, string name, byte[] bytes)
    {
        var entry = manifest["entries"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == name)!;
        entry["length"] = bytes.Length;
        entry["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static byte[] ReadEntry(string archive, string name)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var input = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream(); input.CopyTo(buffer); return buffer.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public TemporaryDirectory Root { get; } = new();
        public string RepositoryPath { get; }
        public LocalDataPaths Paths { get; }
        public RecoveryService Recovery { get; }
        public string IndexPath => Path.Combine(RepositoryPath, ".git", "index");
        public Fixture()
        {
            RepositoryPath = Path.Combine(Root.Path, "repository");
            Directory.CreateDirectory(RepositoryPath);
            Paths = new LocalDataPaths(Path.Combine(Root.Path, "data"));
            Recovery = new RecoveryService(Paths);
            Repository.Init(RepositoryPath);
            using var repository = new Repository(RepositoryPath);
            repository.Config.Set("core.autocrlf", false);
            System.IO.File.WriteAllText(File("a.txt"), "base a");
            System.IO.File.WriteAllText(File("b.txt"), "base b");
            Commands.Stage(repository, new[] { "a.txt", "b.txt" });
            var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
            repository.Commit("base", signature, signature);
        }
        public string File(string name) => Path.Combine(RepositoryPath, name);
        public async Task<RecoveryPoint> SavePointAsync()
        {
            System.IO.File.WriteAllText(File("a.txt"), "saved a");
            System.IO.File.WriteAllText(File("b.txt"), "saved b");
            using (var repository = new Repository(RepositoryPath)) Commands.Stage(repository, "a.txt");
            var point = await Recovery.CreateAsync(RepositoryPath, "test");
            System.IO.File.WriteAllText(File("a.txt"), "later a");
            System.IO.File.WriteAllText(File("b.txt"), "later b");
            using (var repository = new Repository(RepositoryPath))
            {
                Commands.Stage(repository, "b.txt");
                var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.Now);
                repository.Commit("later head", signature, signature);
            }
            return point;
        }
        public string Head()
        {
            using var repository = new Repository(RepositoryPath);
            return repository.Head.CanonicalName + "\n" + repository.Head.Tip!.Id.Sha;
        }
        public string[] References()
        {
            using var repository = new Repository(RepositoryPath);
            return repository.Refs.Where(reference => reference.CanonicalName.StartsWith("refs/gitvisualizer/recovery/"))
                .Select(reference => reference.CanonicalName).Order().ToArray();
        }
        public string State()
        {
            var files = Directory.GetFiles(RepositoryPath, "*", SearchOption.TopDirectoryOnly).Order()
                .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))));
            return Head() + "\n" + string.Join("\n", files) + "\n" +
                (System.IO.File.Exists(IndexPath) ? Convert.ToHexString(System.IO.File.ReadAllBytes(IndexPath)) : "missing-index") + "\n" + string.Join("\n", References());
        }
        public void Dispose() => Root.Dispose();
    }
}
