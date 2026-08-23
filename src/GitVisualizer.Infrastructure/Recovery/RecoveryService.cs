using System.IO.Compression;
using System.Text.Json;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Recovery;

public sealed class RecoveryService : IRecoveryService
{
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
        string repositoryPath,
        string operation,
        IReadOnlyList<string>? affectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalPaths.EnsureCreated();
            using var repository = new Repository(repositoryPath);
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            var safeOperation = string.Concat(operation.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var reference = $"refs/gitvisualizer/recovery/{id}-{safeOperation}";
            var headId = repository.Head.Tip?.Id.Sha ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(headId))
            {
                repository.Refs.Add(reference, headId, true);
            }

            var archivePath = Path.Combine(LocalPaths.RecoveryDirectory, id + ".zip");
            var changedPaths = repository.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true
            }).Select(entry => entry.FilePath);
            var paths = (affectedPaths ?? []).Concat(changedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var manifest = new RecoveryManifest(
                id, repositoryPath, operation, headId, reference, DateTimeOffset.UtcNow, paths);

            await using (var archiveStream = File.Create(archivePath))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(
                            manifestStream, manifest, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var relativePath in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, relativePath));
                    if (!IsWithin(repository.Info.WorkingDirectory, fullPath) || !File.Exists(fullPath))
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry(
                        "files/" + relativePath.Replace('\\', '/'), CompressionLevel.Fastest);
                    await using var input = File.OpenRead(fullPath);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                var indexPath = repository.Info.Path is null
                    ? null
                    : Path.Combine(repository.Info.Path, "index");
                if (indexPath is not null && File.Exists(indexPath))
                {
                    var indexEntry = archive.CreateEntry("git-index", CompressionLevel.Fastest);
                    await using var input = File.OpenRead(indexPath);
                    await using var output = indexEntry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            var info = new FileInfo(archivePath);
            await PruneCoreAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryPoint(
                id, repositoryPath, operation, headId, reference,
                archivePath, manifest.CreatedAt, info.Length, true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<GitOperationResult> RestoreAsync(
        RecoveryPoint point,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(point.ArchivePath))
        {
            return GitOperationResult.Fail(
                "restore", "git switch -c recovered/<time> <saved-head>",
                new FileNotFoundException("恢复归档不存在。", point.ArchivePath));
        }

        // Keep the selected archive from being the oldest item pruned while the
        // mandatory pre-restore safety point is created.
        File.SetLastWriteTimeUtc(point.ArchivePath, DateTime.UtcNow);
        RecoveryPoint? beforeRestore = null;
        try
        {
            beforeRestore = await CreateAsync(
                point.RepositoryPath, "before-recovery-restore", null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return GitOperationResult.Fail(
                "restore", "create recovery point before restore", exception);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(point.ArchivePath))
            {
                throw new FileNotFoundException("恢复归档不存在。", point.ArchivePath);
            }
            RecoveryManifest manifest;
            using (var manifestArchive = ZipFile.OpenRead(point.ArchivePath))
            {
                var manifestEntry = manifestArchive.GetEntry("manifest.json")
                                    ?? throw new InvalidDataException("恢复归档缺少清单。");
                using var manifestStream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<RecoveryManifest>(
                               manifestStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidDataException("恢复归档清单无效。");
            }
            if (!Path.GetFullPath(manifest.RepositoryPath).Equals(
                    Path.GetFullPath(point.RepositoryPath), StringComparison.OrdinalIgnoreCase) ||
                !manifest.Id.Equals(point.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("恢复归档与所选恢复点不匹配。");
            }
            if (string.IsNullOrWhiteSpace(manifest.HeadId))
            {
                throw new InvalidOperationException("该恢复点没有基准提交，无法创建安全恢复分支。");
            }

            string branchName;
            string workingDirectory;
            string indexPath;
            using (var repository = new Repository(point.RepositoryPath))
            {
                if (repository.Info.CurrentOperation != CurrentOperation.None)
                {
                    throw new InvalidOperationException("仓库有尚未结束的 Git 操作，请先继续或中止后再恢复。");
                }
                var commit = repository.Lookup<Commit>(manifest.HeadId)
                             ?? throw new InvalidDataException("恢复点引用的提交不存在。");
                branchName = NextRecoveryBranchName(repository, point.CreatedAt);
                var branch = repository.CreateBranch(branchName, commit);
                Commands.Checkout(repository, branch, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force
                });
                workingDirectory = repository.Info.WorkingDirectory;
                indexPath = Path.Combine(repository.Info.Path, "index");
            }

            using var archive = ZipFile.OpenRead(point.ArchivePath);
            var archivedFiles = archive.Entries
                .Where(entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal) &&
                                !entry.FullName.EndsWith("/", StringComparison.Ordinal))
                .ToDictionary(
                    entry => entry.FullName["files/".Length..].Replace('/', Path.DirectorySeparatorChar),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var relativePath in manifest.AffectedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
                var destination = SafeDestination(workingDirectory, normalized);
                if (!archivedFiles.ContainsKey(normalized) && File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }

            foreach (var (relative, entry) in archivedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = SafeDestination(workingDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }

            var savedIndex = archive.GetEntry("git-index")
                             ?? throw new InvalidDataException("恢复归档缺少暂存区快照。");
            var temporaryIndex = indexPath + $".restore-{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var source = savedIndex.Open())
                await using (var destination = File.Create(temporaryIndex))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryIndex, indexPath, true);
            }
            finally
            {
                if (File.Exists(temporaryIndex))
                {
                    File.Delete(temporaryIndex);
                }
            }

            using (var validationRepository = new Repository(point.RepositoryPath))
            {
                _ = validationRepository.RetrieveStatus();
            }

            return GitOperationResult.Ok(
                "restore",
                $"已在分支 {branchName} 恢复工作区和暂存区",
                $"git switch -c {branchName} {point.HeadId}",
                [
                    $"已恢复：{point.Id}",
                    $"当前分支：{branchName}",
                    $"恢复前保护点：{beforeRestore.Id}"
                ],
                recoveryPointId: beforeRestore.Id);
        }
        catch (Exception exception)
        {
            return GitOperationResult.Fail(
                       "restore", "git switch -c recovered/<time> <saved-head>", exception)
                   with
                   {
                       RecoveryPointId = beforeRestore.Id,
                       Details = [exception.Message, $"恢复前保护点：{beforeRestore.Id}"]
                   };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<RecoveryPoint>> ListAsync(
        string? repositoryPath = null,
        CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        var points = new List<RecoveryPoint>();
        foreach (var file in Directory.EnumerateFiles(LocalPaths.RecoveryDirectory, "*.zip"))
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
            catch (InvalidDataException)
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

    private static async Task PruneCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var files = Directory.EnumerateFiles(LocalPaths.RecoveryDirectory, "*.zip")
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
            if (expired || overCount || overSize)
            {
                File.Delete(info.FullName);
            }
            else
            {
                retainedBytes += info.Length;
            }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeDestination(string workingDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("恢复归档包含绝对路径。");
        }
        var destination = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
        if (!IsWithin(workingDirectory, destination))
        {
            throw new InvalidDataException("恢复归档包含越界路径。");
        }
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
        IReadOnlyList<string> AffectedPaths);
}
