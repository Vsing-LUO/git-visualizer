// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitDiffService : IDiffService, ICommitDiffMetadataService
{
    public const long InputLimit = 8L * 1024 * 1024;
    public const int DisplayLineLimit = 20_000;
    internal const string BudgetNotice = "差异超过 8 MiB 输入或 20,000 行展示预算，无法展示或执行分块操作；仍可整文件暂存或使用外部工具。";

    internal static bool IsBudgetLimited(string patch) => patch.StartsWith(BudgetNotice, StringComparison.Ordinal);
    private static string LimitDisplay(string patch)
    {
        int lines = patch.Length == 0 ? 0 : 1;
        foreach (var c in patch) if (c == '\n' && ++lines > DisplayLineLimit) return BudgetNotice;
        return patch;
    }
    private static long BlobSize(Repository repository, ObjectId? id) => id is null ? 0 : repository.ObjectDatabase.RetrieveObjectMetadata(id).Size;
    private static long TreeSize(Repository repository, Tree? tree, string path) => tree?[path]?.Target is Blob blob ? repository.ObjectDatabase.RetrieveObjectMetadata(blob.Id).Size : 0;

    public Task<DiffPresentation> GetCommitDiffMetadataAsync(string repositoryPath, string oldCommitId,
        string newCommitId, CancellationToken token) => Task.Run(() =>
    {
        using var repository = new Repository(repositoryPath);
        var oldCommit = repository.Lookup<Commit>(oldCommitId) ?? throw new ArgumentException("旧提交不存在。");
        var newCommit = repository.Lookup<Commit>(newCommitId) ?? throw new ArgumentException("新提交不存在。");
        using var changes = repository.Diff.Compare<TreeChanges>(oldCommit.Tree, newCommit.Tree,
            new CompareOptions { Similarity = SimilarityOptions.None });
        var files = changes.Select(change =>
        {
            token.ThrowIfCancellationRequested();
            return new DiffFilePresentation(change.Path, change.OldPath, change.Status switch
                { ChangeKind.Added => DiffFileChangeKind.Added, ChangeKind.Deleted => DiffFileChangeKind.Deleted,
                  ChangeKind.Renamed => DiffFileChangeKind.Renamed, _ => DiffFileChangeKind.Modified },
                change.Status.ToString(), "展开以加载此文件的差异", "旧提交", "新提交", false, [], []);
        }).ToArray();
        return new DiffPresentation("提交比较", $"{files.Length} 个文件，展开后按文件加载。", "旧提交", "新提交", files, "");
    }, token);

    public Task<DiffPresentation> GetWorkingDiffPresentationAsync(
        string repositoryPath,
        string path,
        bool staged,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var patch = GetPatch(repository, path, staged);
            if (IsBudgetLimited(patch)) return Limited(path);
            var hunks = UnifiedDiffParser.Parse(
                path,
                patch,
                staged,
                ComputeSnapshot(repository, path));
            var oldLabel = staged ? "最近提交" : "暂存区";
            var newLabel = staged ? "暂存区" : "工作区";
            return NaturalLanguageDiffParser.Parse(
                patch,
                $"{oldLabel} → {newLabel}",
                oldLabel,
                newLabel,
                hunks,
                path);
        }, cancellationToken);

    public Task<DiffPresentation> CompareCommitsPresentationAsync(
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
            var patch = GetCommitPatch(repository, oldCommit.Tree, newCommit.Tree, path);
            if (IsBudgetLimited(patch)) return Limited(path ?? "提交比较");
            var oldLabel = $"旧提交 {oldCommit.Id.Sha[..8]}";
            var newLabel = $"新提交 {newCommit.Id.Sha[..8]}";
            return NaturalLanguageDiffParser.Parse(
                patch,
                $"{oldLabel} → {newLabel}",
                oldLabel,
                newLabel,
                fallbackPath: path);
        }, cancellationToken);

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
            if (IsBudgetLimited(patch)) return Array.Empty<DiffHunk>();
            return UnifiedDiffParser.Parse(
                path,
                patch,
                staged,
                ComputeSnapshot(repository, path));
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
            var patch = GetPatch(repository, path, staged);
            return IsBudgetLimited(patch) ? patch : GitPatchDisplayFormatter.Format(patch, path, staged);
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
            var patch = GetCommitPatch(repository, oldCommit.Tree, newCommit.Tree, path);
            if (IsBudgetLimited(patch)) return patch;
            return GitPatchDisplayFormatter.FormatCommitComparison(
                patch,
                oldCommit.Id.Sha,
                newCommit.Id.Sha);
        }, cancellationToken);

    internal static string GetPatch(Repository repository, string path, bool staged)
    {
        var fullPath = Path.Combine(repository.Info.WorkingDirectory, path);
        using var worktreeGuard = !staged && File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var worktreeSize = worktreeGuard?.Length ?? 0;
        if (worktreeSize + BlobSize(repository, repository.Index[path]?.Id)
            + (staged ? TreeSize(repository, repository.Head.Tip?.Tree, path) : 0) > InputLimit) return BudgetNotice;
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
        using (patch) return LimitDisplay(patch.Content);
    }

    private static string GetCommitPatch(
        Repository repository,
        Tree oldTree,
        Tree newTree,
        string? path)
    {
        if (path is null)
        {
            using var changes = repository.Diff.Compare<TreeChanges>(oldTree, newTree,
                new CompareOptions { Similarity = SimilarityOptions.None });
            long bytes = 0;
            foreach (var change in changes)
            {
                bytes += TreeSize(repository, oldTree, change.OldPath) + TreeSize(repository, newTree, change.Path);
                if (bytes > InputLimit) return BudgetNotice;
            }
        }
        else if (TreeSize(repository, oldTree, path) + TreeSize(repository, newTree, path) > InputLimit) return BudgetNotice;
        var options = new CompareOptions { Similarity = path is null ? SimilarityOptions.Renames : SimilarityOptions.None };
        var patch = path is null
            ? repository.Diff.Compare<Patch>(oldTree, newTree, options)
            : repository.Diff.Compare<Patch>(oldTree, newTree, [path], options);
        using (patch) return LimitDisplay(patch.Content);
    }

    private static DiffPresentation Limited(string path) => new("文件差异", BudgetNotice, "旧版本", "新版本",
        [new DiffFilePresentation(path, null, DiffFileChangeKind.Modified, "超出预算", BudgetNotice,
            "旧版本", "新版本", false, [], [BudgetNotice])], BudgetNotice);

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
