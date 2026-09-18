using System.IO.Compression;
using System.Text.Json;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Recovery;

public sealed partial class RecoveryService : IRecoveryService
{
    private readonly LocalDataPaths dataPaths;

    public RecoveryService(LocalDataPaths? paths = null) => dataPaths = paths ?? LocalPaths.Default;

    internal Action<string, string>? Checkpoint { get; set; }
    internal long ByteLimit { get; set; } = MaxTotalBytes;
    internal Func<string, long> AvailableDiskBytes { get; set; } = path =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxPoints = 50;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<RecoveryPoint> CreateAsync(
        string repositoryPath, string operation, IReadOnlyList<string>? affectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryGate = Git.GitServiceSupport.LockFor(repositoryPath);
        await repositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateUnderWriteLockAsync(repositoryPath, operation, affectedPaths, cancellationToken).ConfigureAwait(false); }
        finally { repositoryGate.Release(); }
    }

    internal async Task<RecoveryPoint> CreateUnderWriteLockAsync(string repositoryPath, string operation,
        IReadOnlyList<string>? affectedPaths, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateCoreAsync(repositoryPath, operation, affectedPaths, [], cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    public async Task<GitOperationResult> RestoreAsync(RecoveryPoint point, CancellationToken cancellationToken = default)
    {
        var repositoryGate = GitVisualizer.Infrastructure.Git.GitServiceSupport.LockFor(point.RepositoryPath);
        bool repositoryLocked = false, locked = false, mutated = false;
        RecoveryPoint? safety = null;
        string stage = "preflight", staging = string.Empty;
        string? journal = null;
        try
        {
            await repositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            repositoryLocked = true;
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            locked = true;
            dataPaths.EnsureCreated();
            staging = Path.Combine(dataPaths.RecoveryDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var manifest = await ValidateAndExtractAsync(point, staging, cancellationToken).ConfigureAwait(false);
            string[] impacted;
            string originalHead;
            using (var repository = new Repository(point.RepositoryPath))
            {
                EnsureRestorableRepository(repository, manifest, staging);
                originalHead = HeadState(repository);
                impacted = TreePaths(repository.Lookup<Commit>(manifest.HeadId)!.Tree)
                    .Concat(repository.Head.Tip is null ? [] : TreePaths(repository.Head.Tip.Tree))
                    .Concat(manifest.AffectedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var relative in impacted) SafeDestination(repository.Info.WorkingDirectory, relative);
            }
            Checkpoint?.Invoke("preflight-complete", point.RepositoryPath);
            stage = "safety-point";
            safety = await CreateCoreAsync(point.RepositoryPath, "before-recovery-restore", impacted,
                [point.Id], cancellationToken).ConfigureAwait(false);
            // Pin before any repository mutation. Explicit deletion can release a retained safety point.
            await WriteDurableAsync(safety.ArchivePath + ".pin", System.Text.Encoding.UTF8.GetBytes(point.Id), CancellationToken.None);
            journal = Path.Combine(dataPaths.RecoveryDirectory, "restore-" + Guid.NewGuid().ToString("N") + ".json");
            await RecordAsync("prepared", false);
            cancellationToken.ThrowIfCancellationRequested();
            using (var repository = new Repository(point.RepositoryPath))
            {
                EnsureRestorableRepository(repository, manifest, staging);
                if (HeadState(repository) != originalHead) throw new IOException("恢复准备期间 HEAD 已变化。");
                await VerifySnapshotAsync(safety, cancellationToken).ConfigureAwait(false);
                foreach (var relative in impacted) SafeDestination(repository.Info.WorkingDirectory, relative);
                stage = "checkout";
                await RecordAsync(stage, true);
                Checkpoint?.Invoke(stage, point.RepositoryPath);
                cancellationToken.ThrowIfCancellationRequested();
                mutated = true;
                var branchName = NextRecoveryBranchName(repository, point.CreatedAt);
                var branch = repository.CreateBranch(branchName, repository.Lookup<Commit>(manifest.HeadId)!);
                Commands.Checkout(repository, branch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                stage = "worktree";
                await RecordAsync(stage, true);
                foreach (var relative in manifest.AffectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = SafeDestination(repository.Info.WorkingDirectory, relative);
                    Checkpoint?.Invoke("restore-file", destination);
                    var staged = SafeStagedPath(staging, relative);
                    if (File.Exists(staged))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await ReplaceFromStagingAsync(staged, destination, cancellationToken).ConfigureAwait(false);
                    }
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                stage = "index";
                await RecordAsync(stage, true);
                Checkpoint?.Invoke(stage, point.RepositoryPath);
                cancellationToken.ThrowIfCancellationRequested();
                await ReplaceFromStagingAsync(Path.Combine(staging, "git-index"),
                    Path.Combine(repository.Info.Path, "index"), cancellationToken, isIndex: true).ConfigureAwait(false);
                stage = "validation";
                await RecordAsync(stage, true);
                using var validation = new Repository(point.RepositoryPath);
                if (validation.Head.Tip?.Id.Sha != manifest.HeadId || validation.Head.FriendlyName != branchName)
                    throw new IOException("恢复后 HEAD 校验失败。");
                await VerifyExtractedResultAsync(manifest, staging, validation, cancellationToken).ConfigureAwait(false);
                _ = validation.RetrieveStatus();
                await RecordAsync("completed", true);
                return GitOperationResult.Ok("restore", $"已在分支 {branchName} 恢复工作区和暂存区",
                    $"git switch -c {branchName} {point.HeadId}",
                    [$"已恢复：{point.Id}", $"恢复前保护点：{safety.Id}", $"执行记录：{journal}"], recoveryPointId: safety.Id)
                    with { ExecutionStage = "completed", ExecutionRecordPath = journal };
            }
        }
        catch (Exception exception)
        {
            string? recordError = null;
            try { if (journal is not null) await RecordAsync(stage + "-failed", mutated, exception.Message); }
            catch (Exception error) { recordError = "执行记录更新失败，保留上一个持久化阶段：" + error.Message; }
            return GitOperationResult.Fail("restore", "restore recovery point", exception) with
            {
                Summary = mutated ? "恢复未完成，仓库可能已部分修改" : "恢复已中止，尚未修改仓库内容",
                OutcomeOverride = mutated ? GitOperationOutcome.PartiallyCompleted :
                    exception is OperationCanceledException ? GitOperationOutcome.CanceledBeforeExecution : GitOperationOutcome.Failed,
                RecoveryPointId = safety?.Id, ExecutionStage = stage, ExecutionRecordPath = journal,
                Details = new[] { exception.Message, $"停止阶段：{stage}", $"恢复前保护点：{safety?.Id ?? "尚未创建"}",
                    "未自动执行二次强制恢复。", recordError ?? string.Empty }
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (locked) Gate.Release();
            if (repositoryLocked) repositoryGate.Release();
        }

        async Task RecordAsync(string currentStage, bool mayHaveChanged, string? error = null)
        {
            if (journal is null) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, pointId = point.Id,
                repositoryPath = point.RepositoryPath, safetyPointId = safety?.Id, stage = currentStage,
                repositoryMayHaveChanged = mayHaveChanged, error, updatedAt = DateTimeOffset.UtcNow }, JsonOptions);
            await WriteDurableAsync(journal, bytes, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RecoveryPoint>> ListAsync(
        string? repositoryPath = null,
        CancellationToken cancellationToken = default)
    {
        dataPaths.EnsureCreated();
        var points = new List<RecoveryPoint>();
        foreach (var file in Directory.EnumerateFiles(dataPaths.RecoveryDirectory, "*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ZipFile.OpenRead(file);
                var entry = archive.GetEntry("manifest.json");
                if (entry is null)
                {
                    continue;
                }

                await using var stream = entry.Open();
                var manifest = await JsonSerializer.DeserializeAsync<RecoveryManifest>(
                        stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (manifest is null ||
                    (repositoryPath is not null &&
                     !Path.GetFullPath(manifest.RepositoryPath)
                         .Equals(Path.GetFullPath(repositoryPath), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                points.Add(new RecoveryPoint(
                    manifest.Id,
                    manifest.RepositoryPath,
                    manifest.Operation,
                    manifest.HeadId,
                    manifest.ReferenceName,
                    file,
                    manifest.CreatedAt,
                    new FileInfo(file).Length,
                    true));
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            {
                // Ignore a partial or damaged archive; diagnostics can report it separately.
            }
        }

        return points.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PruneCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<GitOperationResult> DeleteAsync(
        RecoveryPoint point,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(point.ArchivePath))
            {
                return GitOperationResult.Fail(
                    "recovery-delete", "delete recovery point",
                    new FileNotFoundException("恢复归档不存在。", point.ArchivePath));
            }

            await DeleteArchiveAndReferenceAsync(
                new FileInfo(point.ArchivePath), cancellationToken).ConfigureAwait(false);
            return GitOperationResult.Ok(
                "recovery-delete", $"已删除恢复点 {point.Id}", "delete recovery point");
        }
        catch (Exception exception)
        {
            return GitOperationResult.Fail("recovery-delete", "delete recovery point", exception);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task PruneRepositoryReferencesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RetryPendingReferenceCleanupAsync(repositoryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!Repository.IsValid(repositoryPath))
            {
                return;
            }

            var retainedRecoveryReferences = await ReadManifestsAsync(cancellationToken)
                .ConfigureAwait(false);
            var retained = retainedRecoveryReferences
                .Where(manifest => PathsEqual(manifest.RepositoryPath, repositoryPath))
                .Select(manifest => manifest.ReferenceName)
                .ToHashSet(StringComparer.Ordinal);

            var archiveIds = Directory.GetFiles(dataPaths.RecoveryDirectory, "*.zip")
                .Select(Path.GetFileNameWithoutExtension).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
            using var repository = new Repository(repositoryPath);
            foreach (var reference in repository.Refs
                         .Where(reference => reference.CanonicalName.StartsWith(
                             "refs/gitvisualizer/recovery/", StringComparison.Ordinal))
                         .ToArray())
            {
                if (!retained.Contains(reference.CanonicalName) && !archiveIds.Any(id =>
                    reference.CanonicalName.StartsWith("refs/gitvisualizer/recovery/" + id + "-", StringComparison.Ordinal)))
                {
                    repository.Refs.Remove(reference.CanonicalName);
                }
            }

            PruneSafetyCategory(repository, "stash-backup");
            PruneSafetyCategory(repository, "remote-recovery");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task PruneCoreAsync(CancellationToken cancellationToken)
    {
        await RetryPendingReferenceCleanupAsync(null, cancellationToken).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(dataPaths.RecoveryDirectory, "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();
        long retainedBytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = files[i];
            var expired = DateTimeOffset.UtcNow - info.LastWriteTimeUtc > MaxAge;
            var overCount = i >= MaxPoints;
            var overSize = retainedBytes + info.Length > MaxTotalBytes;
            if (!File.Exists(info.FullName + ".pin") && (expired || overCount || overSize))
            {
                await DeleteArchiveAndReferenceAsync(info, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                retainedBytes += info.Length;
            }
        }
    }

    private async Task DeleteArchiveAndReferenceAsync(
        FileInfo info,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        File.Delete(info.FullName);
        if (File.Exists(info.FullName + ".pin")) File.Delete(info.FullName + ".pin");
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ReferenceName))
        {
            return;
        }

        try
        {
            RemoveReference(manifest);
        }
        catch
        {
            var pending = Path.Combine(dataPaths.RecoveryDirectory, manifest.Id + ".cleanup");
            await File.WriteAllTextAsync(
                pending,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RemoveReference(RecoveryManifest manifest)
    {
        if (!manifest.ReferenceName.StartsWith("refs/gitvisualizer/recovery/", StringComparison.Ordinal) ||
            !Repository.IsValid(manifest.RepositoryPath))
        {
            return;
        }

        using var repository = new Repository(manifest.RepositoryPath);
        if (repository.Refs[manifest.ReferenceName] is not null)
        {
            repository.Refs.Remove(manifest.ReferenceName);
        }
    }

    private async Task RetryPendingReferenceCleanupAsync(
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        dataPaths.EnsureCreated();
        foreach (var path in Directory.EnumerateFiles(dataPaths.RecoveryDirectory, "*.cleanup"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<RecoveryManifest>(json, JsonOptions);
                if (manifest is null ||
                    (repositoryPath is not null && !PathsEqual(manifest.RepositoryPath, repositoryPath)))
                {
                    continue;
                }
                RemoveReference(manifest);
                File.Delete(path);
            }
            catch (JsonException)
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Leave the cleanup record for the next repository open or prune pass.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave the cleanup record for the next repository open or prune pass.
            }
        }
    }

    private async Task<IReadOnlyList<RecoveryManifest>> ReadManifestsAsync(
        CancellationToken cancellationToken)
    {
        var manifests = new List<RecoveryManifest>();
        foreach (var path in Directory.EnumerateFiles(dataPaths.RecoveryDirectory, "*.zip"))
        {
            var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }
        return manifests;
    }

    private static async Task<RecoveryManifest?> ReadManifestAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry("manifest.json");
            if (entry is null)
            {
                return null;
            }
            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<RecoveryManifest>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static void PruneSafetyCategory(Repository repository, string category)
    {
        var prefix = $"refs/gitvisualizer/{category}/";
        var references = repository.Refs
            .Where(reference => reference.CanonicalName.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(reference => reference.CanonicalName, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < references.Length; index++)
        {
            var name = references[index].CanonicalName;
            var token = name[prefix.Length..].Split('-', 2)[0];
            var expired = DateTimeOffset.TryParseExact(
                token, "yyyyMMddHHmmssfff", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var createdAt) &&
                DateTimeOffset.UtcNow - createdAt > MaxAge;
            if (index >= MaxPoints || expired)
            {
                repository.Refs.Remove(name);
            }
        }
    }

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeDestination(string workingDirectory, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var destination = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
        GitVisualizer.Infrastructure.FileSystem.RepositoryPathGuard.EnsureSafe(workingDirectory, destination, true);
        return destination;
    }

    private static string NextRecoveryBranchName(Repository repository, DateTimeOffset createdAt)
    {
        var baseName = $"recovered/{createdAt:yyyyMMdd-HHmmss}";
        var name = baseName;
        for (var suffix = 2; repository.Branches[name] is not null; suffix++)
        {
            name = $"{baseName}-{suffix}";
        }
        return name;
    }

    private sealed record RecoveryManifest(
        string Id,
        string RepositoryPath,
        string Operation,
        string HeadId,
        string ReferenceName,
        DateTimeOffset CreatedAt,
        IReadOnlyList<string> AffectedPaths,
        int Version = 1,
        IReadOnlyList<RecoveryEntry>? Entries = null,
        string? HeadState = null);

    private sealed record RecoveryEntry(string Name, bool Exists, long Length, string? Sha256);
}
