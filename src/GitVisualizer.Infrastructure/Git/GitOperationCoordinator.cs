using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

using static GitVisualizer.Infrastructure.Git.GitRepositorySupport;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class GitOperationCoordinator
{
    internal GitOperationCoordinator(IRecoveryService recovery, IOperationLogStore log)
    { recoveryService = recovery; operationLog = log; }
    private readonly IRecoveryService recoveryService;

    internal readonly IOperationLogStore operationLog;

    internal async Task<GitOperationResult> ExecuteWriteAsync(
        string repositoryPath,
        string operation,
        string equivalentCommand,
        GitOperationRisk risk,
        bool createRecoveryPoint,
        IReadOnlyList<string>? affectedPaths,
        Func<Repository, GitOperationResult> action,
        bool initializeIfNeeded = false,
        CancellationToken cancellationToken = default)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        var gate = GitServiceSupport.LockFor(repositoryPath);
        bool locked = false, started = false;
        RecoveryPoint? recoveryPoint = null;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            locked = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (initializeIfNeeded && !Repository.IsValid(repositoryPath))
            {
                Directory.CreateDirectory(repositoryPath);
                var gitDirectory = Repository.Init(repositoryPath);
                File.WriteAllText(
                    Path.Combine(gitDirectory, "HEAD"),
                    "ref: refs/heads/main\n",
                    new System.Text.UTF8Encoding(false));
            }
            if (createRecoveryPoint && Repository.IsValid(repositoryPath))
            {
                recoveryPoint = recoveryService is Recovery.RecoveryService localRecovery
                    ? await localRecovery.CreateUnderWriteLockAsync(repositoryPath, operation, affectedPaths, cancellationToken).ConfigureAwait(false)
                    : await recoveryService.CreateAsync(repositoryPath, operation, affectedPaths, cancellationToken).ConfigureAwait(false);
            }

            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var repository = new Repository(repositoryPath);
                started = true;
                return action(repository);
            }, cancellationToken).ConfigureAwait(false);
            result = result with { RecoveryPointId = recoveryPoint?.Id };
            return await Diagnostics.OperationResultLogging.PersistAsync(result,
                value => LogAsync(repositoryPath, value, risk, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var result = (exception is OperationCanceledException && started
                ? GitOperationResult.Interrupted(operation, equivalentCommand, exception)
                : GitOperationResult.Fail(operation, equivalentCommand, exception)) with { RecoveryPointId = recoveryPoint?.Id };
            return await Diagnostics.OperationResultLogging.PersistAsync(result,
                value => LogAsync(repositoryPath, value, risk, CancellationToken.None)).ConfigureAwait(false);
        }
        finally { if (locked) gate.Release(); }
    }

    internal async Task LogAsync(
        string repositoryPath,
        GitOperationResult result,
        GitOperationRisk risk,
        CancellationToken cancellationToken)
    {
        await operationLog.AddAsync(new OperationLogEntry(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            repositoryPath,
            result.Operation,
            result.Success,
            risk,
            result.Summary,
            result.EquivalentCommand,
            result.RecoveryPointId,
            result.ErrorCode,
            result.Details), cancellationToken).ConfigureAwait(false);
    }
}
