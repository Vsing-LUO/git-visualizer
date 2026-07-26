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

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<RecoveryPoint> CreateAsync(
        string repositoryPath,
        string operation,
        IReadOnlyList<string>? affectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            var paths = affectedPaths is { Count: > 0 }
                ? affectedPaths
                : repository.RetrieveStatus(new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true
                })
                    .Select(x => x.FilePath)
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
            gate.Release();
        }
    }

    public async Task<GitOperationResult> RestoreAsync(
        RecoveryPoint point,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(point.ArchivePath))
            {
                throw new FileNotFoundException("恢复归档不存在。", point.ArchivePath);
            }

            using var repository = new Repository(point.RepositoryPath);
            var branchName = $"recovered/{point.CreatedAt:yyyyMMdd-HHmmss}";
            if (!string.IsNullOrWhiteSpace(point.HeadId) && repository.Branches[branchName] is null)
            {
                var commit = repository.Lookup<Commit>(point.HeadId);
                if (commit is not null)
                {
                    repository.CreateBranch(branchName, commit);
                }
            }

            using var archive = ZipFile.OpenRead(point.ArchivePath);
            foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith("files/", StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = entry.FullName["files/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, relative));
                if (!IsWithin(repository.Info.WorkingDirectory, destination))
                {
                    throw new InvalidDataException("恢复归档包含越界路径。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }

            return GitOperationResult.Ok(
                "restore",
                $"已恢复工作区文件，并创建分支 {branchName}",
                $"git branch {branchName} {point.HeadId}",
                [$"恢复点：{point.Id}", $"安全分支：{branchName}"]);
        }
        catch (Exception exception)
        {
            return GitOperationResult.Fail("restore", "git branch <recovery-branch> <saved-head>", exception);
        }
        finally
        {
            gate.Release();
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PruneCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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

    private sealed record RecoveryManifest(
        string Id,
        string RepositoryPath,
        string Operation,
        string HeadId,
        string ReferenceName,
        DateTimeOffset CreatedAt,
        IReadOnlyList<string> AffectedPaths);
}
