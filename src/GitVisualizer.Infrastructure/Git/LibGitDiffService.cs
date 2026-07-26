using System.Security.Cryptography;
using System.Text;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitDiffService : IDiffService
{
    public Task<IReadOnlyList<DiffHunk>> GetWorkingDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DiffHunk>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var patch = GetPatch(repository, path, staged);
            var snapshot = ComputeSnapshot(repository, path);
            return UnifiedDiffParser.Parse(path, patch, staged, snapshot);
        }, cancellationToken);

    public Task<string> GetUnifiedDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            return GetPatch(repository, path, staged);
        }, cancellationToken);

    public Task<string> CompareCommitsAsync(
        string repositoryPath,
        string oldCommitId,
        string newCommitId,
        string? path = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var oldCommit = repository.Lookup<Commit>(oldCommitId)
                            ?? throw new ArgumentException("旧提交不存在。");
            var newCommit = repository.Lookup<Commit>(newCommitId)
                            ?? throw new ArgumentException("新提交不存在。");
            var paths = path is null ? null : new[] { path };
            return repository.Diff.Compare<Patch>(oldCommit.Tree, newCommit.Tree, paths).Content;
        }, cancellationToken);

    internal static string GetPatch(Repository repository, string path, bool staged)
    {
        var paths = new[] { path };
        Patch patch;
        if (staged)
        {
            patch = repository.Diff.Compare<Patch>(
                repository.Head.Tip?.Tree, DiffTargets.Index, paths);
        }
        else
        {
            patch = repository.Diff.Compare<Patch>(paths, true);
        }
        return patch.Content;
    }

    internal static string ComputeSnapshot(Repository repository, string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var indexEntry = repository.Index[path];
        if (indexEntry is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(indexEntry.Id.Sha));
        }
        var fullPath = Path.Combine(repository.Info.WorkingDirectory, path);
        if (File.Exists(fullPath))
        {
            using var stream = File.OpenRead(fullPath);
            Span<byte> buffer = stackalloc byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer[..read]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
