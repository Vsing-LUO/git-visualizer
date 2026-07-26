using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitIndexPatchService : IIndexPatchService
{
    private readonly IOperationLogStore operationLog;
    private NativeGitApply? nativeApply;

    public LibGitIndexPatchService(IOperationLogStore operationLog)
    {
        this.operationLog = operationLog;
    }

    public Task<GitOperationResult> StageHunksAsync(
        string repositoryPath,
        string path,
        IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(repositoryPath, path, hunks, reverse: false, cancellationToken);

    public Task<GitOperationResult> UnstageHunksAsync(
        string repositoryPath,
        string path,
        IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(repositoryPath, path, hunks, reverse: true, cancellationToken);

    private async Task<GitOperationResult> ApplyAsync(
        string repositoryPath,
        string path,
        IReadOnlyList<DiffHunk> hunks,
        bool reverse,
        CancellationToken cancellationToken)
    {
        var operation = reverse ? "hunk-unstage" : "hunk-stage";
        var command = reverse
            ? $"git apply --cached --reverse <selected-hunks:{path}>"
            : $"git apply --cached <selected-hunks:{path}>";
        var gate = GitServiceSupport.LockFor(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (hunks.Count == 0 || hunks.Any(hunk => !hunk.Path.Equals(path, StringComparison.Ordinal)))
            {
                throw new ArgumentException("请选择同一文件中的至少一个差异块。");
            }

            using (var repository = new Repository(repositoryPath))
            {
                var currentSnapshot = LibGitDiffService.ComputeSnapshot(repository, path);
                if (hunks.Any(hunk => !hunk.SnapshotId.Equals(currentSnapshot, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("文件已在差异生成后发生变化，请刷新后重新选择。");
                }
            }

            var patch = UnifiedDiffParser.CombinePatches(hunks);
            if (reverse)
            {
                patch = UnifiedDiffParser.ReversePatch(patch);
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                nativeApply ??= new NativeGitApply();
                nativeApply.ApplyPatchToIndex(repositoryPath, patch);
            }, cancellationToken).ConfigureAwait(false);

            var result = GitOperationResult.Ok(
                operation,
                reverse ? $"已取消暂存 {hunks.Count} 个差异块" : $"已暂存 {hunks.Count} 个差异块",
                command,
                hunks.Select(hunk => hunk.Header));
            await LogAsync(repositoryPath, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var result = GitOperationResult.Fail(operation, command, exception);
            await LogAsync(repositoryPath, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task LogAsync(
        string repositoryPath, GitOperationResult result, CancellationToken cancellationToken) =>
        operationLog.AddAsync(new OperationLogEntry(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            repositoryPath,
            result.Operation,
            result.Success,
            GitOperationRisk.Safe,
            result.Summary,
            result.EquivalentCommand,
            null,
            result.ErrorCode), cancellationToken);
}
