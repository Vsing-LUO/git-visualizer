// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Recovery;

public sealed partial class RecoveryService
{
    private async Task<RecoveryPoint> CreateCoreAsync(string repositoryPath, string operation,
        IReadOnlyList<string>? affectedPaths, IReadOnlyList<string> protectedIds, CancellationToken token)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        dataPaths.EnsureCreated();
        using var repository = new Repository(repositoryPath);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var reference = $"refs/gitvisualizer/recovery/{id}-{string.Concat(operation.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}";
        var headId = repository.Head.Tip?.Id.Sha ?? string.Empty;
        var headState = HeadState(repository);
        var paths = SnapshotPaths(repository, affectedPaths);
        var indexPath = Path.Combine(repository.Info.Path, "index");
        if (!File.Exists(indexPath) && headId.Length != 0) throw new InvalidDataException("仓库缺少必需的 Git 索引，无法创建完整恢复点。");
        if (File.Exists(indexPath)) ValidateGitIndex(repositoryPath, indexPath);
        var sources = paths.Select(path => (Name: "files/" + path, Path: SafeDestination(repository.Info.WorkingDirectory, path)))
            .Append((Name: "git-index", Path: indexPath)).ToArray();
        long estimated = 1024 * 1024;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(source.Path)) estimated = checked(estimated + new FileInfo(source.Path).Length + new FileInfo(source.Path).Length / 100 + 512);
        }
        if (estimated > ByteLimit) throw new IOException("完整备份超过恢复配额，操作已中止；不会跳过大文件。");
        if (AvailableDiskBytes(dataPaths.RecoveryDirectory) < estimated) throw new IOException("磁盘可用空间不足以创建完整恢复点。");
        await ReserveCapacityAsync(estimated, protectedIds, token).ConfigureAwait(false);
        var snapshots = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal);
        foreach (var source in sources) snapshots.Add(source.Name, await CaptureAsync(source.Path, token).ConfigureAwait(false));
        var archivePath = Path.Combine(dataPaths.RecoveryDirectory, id + ".zip");
        var temporary = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool published = false, referenced = false;
        try
        {
            var entries = new List<RecoveryEntry>();
            long copiedBytes = 0;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var bounded = new BoundedWriteStream(file, estimated))
                using (var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var source in sources)
                    {
                        token.ThrowIfCancellationRequested();
                        Checkpoint?.Invoke("backup-before-file", source.Path);
                        var before = snapshots[source.Name];
                        if (before != await CaptureAsync(source.Path, token).ConfigureAwait(false))
                            throw new IOException("备份期间源文件已变化：" + source.Path);
                        if (before.Exists)
                        {
                            var entry = archive.CreateEntry(source.Name, CompressionLevel.Fastest);
                            await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            await using var output = entry.Open();
                            var buffer = new byte[81920];
                            int count;
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            long length = 0;
                            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                            {
                                copiedBytes = checked(copiedBytes + count);
                                if (copiedBytes > ByteLimit) throw new IOException("备份实际体积超过恢复配额。");
                                hash.AppendData(buffer, 0, count);
                                length += count;
                                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                                Checkpoint?.Invoke("backup-write", source.Path);
                            }
                            if (length != before.Length || Convert.ToHexString(hash.GetHashAndReset()) != before.Sha256)
                                throw new IOException("备份读取内容与原始快照不一致：" + source.Path);
                        }
                        Checkpoint?.Invoke("backup-after-file", source.Path);
                        if (before != await CaptureAsync(source.Path, token).ConfigureAwait(false))
                            throw new IOException("备份期间源文件已变化：" + source.Path);
                        entries.Add(new RecoveryEntry(source.Name, before.Exists, before.Length, before.Sha256));
                    }
                    var manifest = new RecoveryManifest(id, repositoryPath, operation, headId, reference,
                        DateTimeOffset.UtcNow, paths, 2, entries, headState);
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using var stream = manifestEntry.Open();
                    await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, token).ConfigureAwait(false);
                }
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(true);
            }
            // Re-read every compressed payload before accepting this as a required backup.
            using (var verification = ZipFile.OpenRead(temporary))
            {
                foreach (var entry in verification.Entries)
                {
                    RecoveryEntry? expected = null;
                    if (snapshots.TryGetValue(entry.FullName, out var snapshot))
                        expected = new RecoveryEntry(entry.FullName, snapshot.Exists, snapshot.Length, snapshot.Sha256);
                    await ExtractCheckedAsync(entry, Stream.Null, expected, token).ConfigureAwait(false);
                }
            }
            Checkpoint?.Invoke("backup-final-check", repositoryPath);
            using (var current = new Repository(repositoryPath))
            {
                if (HeadState(current) != headState || !paths.SequenceEqual(SnapshotPaths(current, affectedPaths), StringComparer.Ordinal))
                    throw new IOException("备份期间仓库状态或文件集合已变化。");
            }
            foreach (var source in sources)
                if (snapshots[source.Name] != await CaptureAsync(source.Path, token).ConfigureAwait(false))
                    throw new IOException("备份期间源文件已变化：" + source.Path);
            token.ThrowIfCancellationRequested();
            var created = await ReadManifestAsync(temporary, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("新建恢复清单不可读取。");
            File.Move(temporary, archivePath);
            published = true;
            if (headId.Length != 0) { repository.Refs.Add(reference, headId, false); referenced = true; }
            return new RecoveryPoint(id, repositoryPath, operation, headId, reference, archivePath,
                created.CreatedAt, new FileInfo(archivePath).Length, true);
        }
        catch
        {
            if (referenced) repository.Refs.Remove(reference);
            if (published && File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ReserveCapacityAsync(long required, IReadOnlyList<string> protectedIds, CancellationToken token)
    {
        var files = Directory.GetFiles(dataPaths.RecoveryDirectory, "*.zip").Select(path => new FileInfo(path))
            .OrderBy(info => info.LastWriteTimeUtc).ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(file.FullName + ".pin") || protectedIds.Contains(Path.GetFileNameWithoutExtension(file.Name))) continue;
            if (total + required <= ByteLimit && files.Count < MaxPoints && DateTimeOffset.UtcNow - file.LastWriteTimeUtc <= MaxAge) continue;
            await DeleteArchiveAndReferenceAsync(file, token).ConfigureAwait(false);
            total -= file.Length;
            files.Remove(file);
        }
        if (total + required > ByteLimit || files.Count >= MaxPoints)
            throw new IOException("恢复配额不足；受保护的恢复前安全点不会被自动删除。");
    }

    private async Task<RecoveryManifest> ValidateAndExtractAsync(RecoveryPoint point, string staging, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(point.ArchivePath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName)) throw new InvalidDataException("恢复归档包含重复条目。");
            if (entry.FullName is not ("manifest.json" or "git-index"))
            {
                if (!entry.FullName.StartsWith("files/", StringComparison.Ordinal)) throw new InvalidDataException("恢复归档包含未知条目。");
                SafeDestination(point.RepositoryPath, entry.FullName[6..]);
            }
        }
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("恢复归档缺少清单。");
        if (manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复清单过大。");
        using var manifestBytes = new MemoryStream();
        await ExtractCheckedAsync(manifestEntry, manifestBytes, null, token).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<RecoveryManifest>(manifestBytes.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("恢复归档清单无效。");
        if (manifest.Version is not (1 or 2) || manifest.AffectedPaths is null ||
            !PathsEqual(manifest.RepositoryPath, point.RepositoryPath) || manifest.Id != point.Id ||
            manifest.HeadId != point.HeadId || manifest.ReferenceName != point.ReferenceName ||
            !manifest.ReferenceName.StartsWith("refs/gitvisualizer/recovery/", StringComparison.Ordinal))
            throw new InvalidDataException("恢复归档清单版本或身份不匹配。");
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.AffectedPaths)
        {
            SafeDestination(point.RepositoryPath, path);
            if (!affected.Add(path.Replace('\\', '/'))) throw new InvalidDataException("恢复清单路径重复。");
        }
        Dictionary<string, RecoveryEntry>? expected = null;
        if (manifest.Version == 2)
        {
            if (manifest.Entries is null) throw new InvalidDataException("恢复清单缺少完整性信息。");
            expected = new Dictionary<string, RecoveryEntry>(StringComparer.Ordinal);
            foreach (var item in manifest.Entries)
            {
                if (!expected.TryAdd(item.Name, item) || item.Length < 0 ||
                    (item.Exists && (item.Sha256 is null || item.Sha256.Length != 64)) ||
                    (!item.Exists && (item.Length != 0 || item.Sha256 is not null)))
                    throw new InvalidDataException("恢复清单长度或摘要无效。");
            }
            var requiredNames = affected.Select(path => "files/" + path).Append("git-index").ToHashSet(StringComparer.Ordinal);
            if (!requiredNames.SetEquals(expected.Keys)) throw new InvalidDataException("恢复清单条目集合不完整。");
            var present = expected.Values.Where(item => item.Exists).Select(item => item.Name).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
            if (!present.SetEquals(archive.Entries.Select(entry => entry.FullName))) throw new InvalidDataException("恢复归档条目缺失或多余。");
        }
        if (archive.GetEntry("git-index") is null) throw new InvalidDataException("恢复归档缺少暂存区快照。");
        long extracted = manifestEntry.Length;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.json"))
        {
            token.ThrowIfCancellationRequested();
            extracted = checked(extracted + entry.Length);
            if (extracted > ByteLimit) throw new InvalidDataException("完整解压体积超过恢复配额。");
            if (entry.FullName != "git-index" && !affected.Contains(entry.FullName[6..]))
                throw new InvalidDataException("归档文件未列入受影响路径。");
            var destination = entry.FullName == "git-index" ? Path.Combine(staging, "git-index") : SafeStagedPath(staging, entry.FullName[6..]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await ExtractCheckedAsync(entry, output, expected?.GetValueOrDefault(entry.FullName), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
        }
        ValidateGitIndex(point.RepositoryPath, Path.Combine(staging, "git-index"));
        return manifest;
    }

    private async Task ExtractCheckedAsync(ZipArchiveEntry entry, Stream destination, RecoveryEntry? expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        uint crc = uint.MaxValue;
        long length = 0;
        var buffer = new byte[81920];
        await using var source = entry.Open();
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            length = checked(length + count);
            if (length > ByteLimit || length > entry.Length) throw new InvalidDataException("ZIP 解压长度超限。");
            hash.AppendData(buffer, 0, count);
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (length != entry.Length || ~crc != entry.Crc32 ||
            (expected is not null && (length != expected.Length || Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)))
            throw new InvalidDataException("恢复归档内容的长度、CRC 或 SHA-256 校验失败：" + entry.FullName);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        uint crc = (uint)value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    private static void ValidateGitIndex(string repositoryPath, string indexPath)
    {
        using (var input = File.OpenRead(indexPath))
        {
            if (input.Length < 32) throw new InvalidDataException("Git 索引长度无效。");
            Span<byte> header = stackalloc byte[12];
            input.ReadExactly(header);
            if (!header[..4].SequenceEqual("DIRC"u8) || BinaryPrimitives.ReadUInt32BigEndian(header[4..8]) is not (2 or 3 or 4))
                throw new InvalidDataException("Git 索引格式无效。");
            input.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long remaining = input.Length - 20;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                int count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            Span<byte> checksum = stackalloc byte[20];
            input.ReadExactly(checksum);
            if (!checksum.SequenceEqual(hash.GetHashAndReset())) throw new InvalidDataException("Git 索引摘要校验失败。");
        }
        using var isolated = new Repository(repositoryPath, new RepositoryOptions { IndexPath = indexPath });
        foreach (var entry in isolated.Index)
        {
            SafeDestination(isolated.Info.WorkingDirectory, entry.Path);
            if (entry.Mode is Mode.GitLink or Mode.SymbolicLink)
                throw new InvalidDataException("恢复索引暂不支持子模块或符号链接，未跳过备份。");
            if (isolated.Lookup<Blob>(entry.Id) is null)
                throw new InvalidDataException("Git 索引引用的文件对象缺失。");
        }
    }

    private static void EnsureRestorableRepository(Repository repository, RecoveryManifest manifest, string staging)
    {
        if (repository.Info.CurrentOperation != CurrentOperation.None) throw new InvalidOperationException("仓库有尚未结束的 Git 操作，请先继续或中止后再恢复。");
        if (string.IsNullOrWhiteSpace(manifest.HeadId) || repository.Lookup<Commit>(manifest.HeadId) is null)
            throw new InvalidDataException("恢复点基准提交不存在。");
        if (repository.Refs[manifest.ReferenceName]?.ResolveToDirectReference().TargetIdentifier != manifest.HeadId)
            throw new InvalidDataException("恢复引用缺失或与基准提交不一致。");
        if (File.Exists(Path.Combine(repository.Info.Path, "index.lock"))) throw new IOException("Git 索引已被占用。");
        ValidateGitIndex(repository.Info.WorkingDirectory, Path.Combine(repository.Info.Path, "index"));
        ValidateGitIndex(repository.Info.WorkingDirectory, Path.Combine(staging, "git-index"));
        var baselinePaths = TreePaths(repository.Lookup<Commit>(manifest.HeadId)!.Tree).ToArray();
        var allPaths = baselinePaths.Concat(manifest.AffectedPaths)
            .Concat(repository.Head.Tip is null ? [] : TreePaths(repository.Head.Tip.Tree))
            .Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allPaths)
        {
            for (var parent = path.LastIndexOf('/'); parent >= 0; parent = path.LastIndexOf('/', parent - 1))
            {
                if (allPaths.Contains(path[..parent]))
                    throw new InvalidDataException("恢复路径存在文件与目录类型冲突，请先人工核对。");
                if (parent == 0) break;
            }
        }
        foreach (var path in baselinePaths)
        {
            var destination = SafeDestination(repository.Info.WorkingDirectory, path);
            ProbeWritable(destination);
        }
        foreach (var path in manifest.AffectedPaths) ProbeWritable(SafeDestination(repository.Info.WorkingDirectory, path));
        ProbeWritable(Path.Combine(repository.Info.Path, "index"));
    }

    private static void ProbeWritable(string path)
    {
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("恢复目标为只读：" + path);
        using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task VerifySnapshotAsync(RecoveryPoint point, CancellationToken token)
    {
        var manifest = await ReadManifestAsync(point.ArchivePath, token).ConfigureAwait(false) ?? throw new InvalidDataException("安全点清单不可读。");
        using var repository = new Repository(point.RepositoryPath);
        if (HeadState(repository) != manifest.HeadState) throw new IOException("创建安全点后 HEAD 已变化。");
        foreach (var entry in manifest.Entries!)
        {
            var path = entry.Name == "git-index" ? Path.Combine(repository.Info.Path, "index") : SafeDestination(repository.Info.WorkingDirectory, entry.Name[6..]);
            var current = await CaptureAsync(path, token).ConfigureAwait(false);
            if (entry.Exists != current.Exists || entry.Length != current.Length || entry.Sha256 != current.Sha256)
                throw new IOException("创建安全点后仓库文件已变化：" + path);
        }
    }

    private async Task VerifyExtractedResultAsync(RecoveryManifest manifest, string staging, Repository repository, CancellationToken token)
    {
        foreach (var relative in manifest.AffectedPaths)
        {
            var expected = await CaptureAsync(SafeStagedPath(staging, relative), token).ConfigureAwait(false);
            var actual = await CaptureAsync(SafeDestination(repository.Info.WorkingDirectory, relative), token).ConfigureAwait(false);
            if (expected.Exists != actual.Exists || expected.Sha256 != actual.Sha256 || expected.Length != actual.Length)
                throw new IOException("恢复后工作区内容校验失败：" + relative);
        }
        var savedIndex = await CaptureAsync(Path.Combine(staging, "git-index"), token).ConfigureAwait(false);
        var currentIndex = await CaptureAsync(Path.Combine(repository.Info.Path, "index"), token).ConfigureAwait(false);
        if (savedIndex.Sha256 != currentIndex.Sha256) throw new IOException("恢复后索引校验失败。");
    }

    private static string HeadState(Repository repository) => repository.Head.CanonicalName + "\n" + (repository.Head.Tip?.Id.Sha ?? "");

    private static string[] SnapshotPaths(Repository repository, IReadOnlyList<string>? affectedPaths)
    {
        var paths = repository.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true })
            .Select(entry => entry.FilePath).Concat(repository.Index.Select(entry => entry.Path))
            .Concat(repository.Head.Tip is null ? [] : TreePaths(repository.Head.Tip.Tree)).Concat(affectedPaths ?? []);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var full = SafeDestination(repository.Info.WorkingDirectory, normalized);
            if (Directory.Exists(full)) AddDirectory(full);
            else result.Add(normalized);
        }
        return result.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        void AddDirectory(string directory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(repository.Info.WorkingDirectory, child).Replace('\\', '/');
                SafeDestination(repository.Info.WorkingDirectory, relative);
                if (Directory.Exists(child)) AddDirectory(child); else result.Add(relative);
            }
        }
    }

    private static IEnumerable<string> TreePaths(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = prefix + entry.Name;
            ValidateRelativePath(path);
            if (entry.TargetType == TreeEntryTargetType.Tree)
                foreach (var child in TreePaths((Tree)entry.Target, path + "/")) yield return child;
            else
            {
                if (entry.Mode is Mode.SymbolicLink or Mode.GitLink)
                    throw new InvalidDataException("恢复快照暂不支持符号链接或子模块，未跳过备份。");
                _ = entry.Target;
                yield return path;
            }
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new InvalidDataException("恢复归档路径无效。");
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("恢复归档包含不安全路径。");
        }
    }

    private static string SafeStagedPath(string staging, string relative)
    {
        ValidateRelativePath(relative);
        return Path.Combine(staging, "files", relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed record FileSnapshot(bool Exists, long Length, string? Sha256, DateTime Modified, DateTime Created, FileAttributes Attributes);

    private static async Task<FileSnapshot> CaptureAsync(string path, CancellationToken token)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new FileSnapshot(false, 0, null, default, default, default); }
        catch (DirectoryNotFoundException) { return new FileSnapshot(false, 0, null, default, default, default); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("快照目标不是普通文件：" + path);
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        var created = info.CreationTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified || info.CreationTimeUtc != created || info.Attributes != attributes)
            throw new IOException("读取期间文件已变化：" + path);
        return new FileSnapshot(true, length, hash, modified, created, attributes);
    }

    private static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ReplaceFromStagingAsync(string staged, string destination, CancellationToken token, bool isIndex = false)
    {
        var original = await CaptureAsync(destination, token).ConfigureAwait(false);
        var temporary = isIndex ? destination + ".lock" : destination + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
        bool ownsTemporary = false;
        try
        {
            await using (var input = File.OpenRead(staged))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    Checkpoint?.Invoke("restore-copy", destination);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (original != await CaptureAsync(destination, token).ConfigureAwait(false))
                throw new IOException("恢复写入期间目标已被外部修改：" + destination);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { Check(value); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Check(Position + buffer.Length); await inner.WriteAsync(buffer, token).ConfigureAwait(false); }
        private void Check(long end) { if (end > limit) throw new IOException("恢复归档实际写入超过预留配额。"); }
    }
}
