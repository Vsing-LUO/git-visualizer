// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

namespace GitVisualizer.Infrastructure.Git;

// Compatibility facade: implementations own their queries, mutations and remote operations.
public sealed class LibGitRepositoryService : IGitRepositoryService, IIncrementalRepositoryService, IHistorySessionService, IConflictDetailsService, IHistoricalFileExportService, ICommitDirectoryService
{
    private readonly GitRepositoryQueries queries;
    private readonly GitRepositoryWrites writes;
    private readonly GitRemoteOperations remotes;
    private readonly IHistoryTraversal history;

    public LibGitRepositoryService(IRecoveryService recoveryService, IOperationLogStore operationLog)
        : this(recoveryService, operationLog, new GitHistoryTraversal()) { }

    internal LibGitRepositoryService(IRecoveryService recoveryService, IOperationLogStore operationLog, IHistoryTraversal history, ISafeFileWriter? safeWriter = null)
    {
        var operations = new GitOperationCoordinator(recoveryService, operationLog);
        queries = new GitRepositoryQueries(operations);
        writes = new GitRepositoryWrites(operations, safeWriter ?? new SafeFileWriter());
        remotes = new GitRemoteOperations(operations);
        this.history = history;
    }

    internal Action? ConflictStageCheckpoint { get => writes.ConflictStageCheckpoint; set => writes.ConflictStageCheckpoint = value; }
    internal static void EnsurePushWasAccepted(
        string destinationRef,
        string localTipId,
        string? remoteTipId,
        IReadOnlyList<string> pushStatusErrors) => GitRepositorySupport.EnsurePushWasAccepted(destinationRef, localTipId, remoteTipId, pushStatusErrors);

    public Task<bool> IsRepositoryAsync(
        string path, CancellationToken cancellationToken = default) =>
        queries.IsRepositoryAsync(path, cancellationToken);

    public Task<GitIdentity?> GetIdentityAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        queries.GetIdentityAsync(repositoryPath, cancellationToken);

    public Task<GitIdentity?> GetDefaultIdentityAsync(CancellationToken cancellationToken = default) =>
        queries.GetDefaultIdentityAsync(cancellationToken);

    public Task<GitOperationResult> SetGlobalIdentityAsync(
        GitIdentity identity,
        CancellationToken cancellationToken = default) =>
        writes.SetGlobalIdentityAsync(identity, cancellationToken);

    public Task<GitOperationResult> SetIdentityAsync(
        string repositoryPath,
        GitIdentity identity,
        bool global,
        CancellationToken cancellationToken = default) =>
        writes.SetIdentityAsync(repositoryPath, identity, global, cancellationToken);

    public Task<GitOperationResult> InitializeAsync(
        string path, GitIdentity? identity = null, CancellationToken cancellationToken = default) =>
        writes.InitializeAsync(path, identity, cancellationToken);

    public Task<GitOperationResult> CloneAsync(
        string url,
        string path,
        RemoteCredential? credential = null,
        CancellationToken cancellationToken = default) =>
        remotes.CloneAsync(url, path, credential, cancellationToken);

    public Task<RepositorySnapshot> GetSnapshotAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        queries.GetSnapshotAsync(repositoryPath, cancellationToken);

    public Task<RepositorySnapshot> GetSnapshotAsync(string repositoryPath, RepositorySnapshot? previous,
        RepositoryChangeKind changesToLoad, CancellationToken cancellationToken) =>
        queries.GetSnapshotAsync(repositoryPath, previous, changesToLoad, cancellationToken);

    public void ResetHistorySession() =>
        history.ResetHistorySession();

    public Task<IReadOnlyList<CommitNode>> GetHistoryAsync(string repositoryPath, int skip, int take,
        CancellationToken cancellationToken = default) =>
        history.GetHistoryAsync(repositoryPath, skip, take, cancellationToken);

    public Task<IReadOnlyList<CommitNode>> GetBranchHistoryAsync(string repositoryPath, string branchName,
        int skip, int take, CancellationToken cancellationToken = default) =>
        history.GetBranchHistoryAsync(repositoryPath, branchName, skip, take, cancellationToken);

    public Task<IReadOnlyList<GitHistoryEvent>> GetHistoryEventsAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default) =>
        queries.GetHistoryEventsAsync(repositoryPath, cancellationToken);

    public Task<IReadOnlyList<CommitTreeEntry>> GetCommitTreeAsync(
        string repositoryPath,
        string commitId,
        CancellationToken cancellationToken = default) =>
        queries.GetCommitTreeAsync(repositoryPath, commitId, cancellationToken);

    public Task<IReadOnlyList<CommitTreeEntry>> GetCommitDirectoryAsync(string repositoryPath, string commitId,
        string directory, CancellationToken token) =>
        queries.GetCommitDirectoryAsync(repositoryPath, commitId, directory, token);

    public Task<TextDocument> OpenCommitFileAsync(
        string repositoryPath,
        string commitId,
        string path,
        CancellationToken cancellationToken = default) =>
        queries.OpenCommitFileAsync(repositoryPath, commitId, path, cancellationToken);

    public Task<GitOperationResult> StageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        writes.StageFilesAsync(repositoryPath, paths, cancellationToken);

    public Task<GitOperationResult> UnstageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        writes.UnstageFilesAsync(repositoryPath, paths, cancellationToken);

    public Task<GitOperationResult> DiscardFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        writes.DiscardFilesAsync(repositoryPath, paths, cancellationToken);

    public Task<GitOperationResult> CommitAsync(
        string repositoryPath,
        string message,
        GitIdentity? identity = null,
        bool amend = false,
        CancellationToken cancellationToken = default) =>
        writes.CommitAsync(repositoryPath, message, identity, amend, cancellationToken);

    public Task<GitOperationResult> CreateBranchAsync(
        string repositoryPath, string name, string? startPoint = null,
        CancellationToken cancellationToken = default) =>
        writes.CreateBranchAsync(repositoryPath, name, startPoint, cancellationToken);

    public Task<GitOperationResult> CheckoutBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        queries.CheckoutBranchAsync(repositoryPath, name, cancellationToken);

    public Task<GitOperationResult> CheckoutCommitAsync(
        string repositoryPath,
        string commitId,
        CancellationToken cancellationToken = default) =>
        queries.CheckoutCommitAsync(repositoryPath, commitId, cancellationToken);

    public Task<GitOperationResult> RenameBranchAsync(
        string repositoryPath, string oldName, string newName,
        CancellationToken cancellationToken = default) =>
        writes.RenameBranchAsync(repositoryPath, oldName, newName, cancellationToken);

    public Task<GitOperationResult> DeleteBranchAsync(
        string repositoryPath, string name, bool force, CancellationToken cancellationToken = default) =>
        writes.DeleteBranchAsync(repositoryPath, name, force, cancellationToken);

    public Task<BranchDeletionCheck> CheckBranchDeletionAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken = default) =>
        queries.CheckBranchDeletionAsync(repositoryPath, name, cancellationToken);

    public Task<GitOperationResult> MergeAsync(
        string repositoryPath, string branchName, GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.MergeAsync(repositoryPath, branchName, identity, cancellationToken);

    public Task<GitOperationResult> CherryPickAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.CherryPickAsync(repositoryPath, commitId, identity, cancellationToken);

    public Task<GitOperationResult> RevertAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.RevertAsync(repositoryPath, commitId, identity, cancellationToken);

    public Task<GitOperationResult> RebaseOntoAsync(
        string repositoryPath,
        string upstreamBranch,
        string? ontoBranch = null,
        GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.RebaseOntoAsync(repositoryPath, upstreamBranch, ontoBranch, identity, cancellationToken);

    public Task<GitOperationResult> ResetAsync(
        string repositoryPath, string targetId, GitResetMode mode,
        CancellationToken cancellationToken = default) =>
        writes.ResetAsync(repositoryPath, targetId, mode, cancellationToken);

    public Task<GitOperationResult> CreateTagAsync(
        string repositoryPath, string name, string? targetId = null,
        GitTagType tagType = GitTagType.Lightweight, string? message = null,
        CancellationToken cancellationToken = default) =>
        writes.CreateTagAsync(repositoryPath, name, targetId, tagType, message, cancellationToken);

    public Task<GitOperationResult> DeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        writes.DeleteTagAsync(repositoryPath, name, cancellationToken);

    public Task<GitOperationResult> SaveStashAsync(
        string repositoryPath, string message, GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.SaveStashAsync(repositoryPath, message, identity, cancellationToken);

    public Task<IReadOnlyList<StashInfo>> GetStashesAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        queries.GetStashesAsync(repositoryPath, cancellationToken);

    public Task<GitOperationResult> ApplyStashAsync(
        string repositoryPath, int index, bool pop, CancellationToken cancellationToken = default) =>
        writes.ApplyStashAsync(repositoryPath, index, pop, cancellationToken);

    public Task<GitOperationResult> DeleteStashAsync(
        string repositoryPath, int index, CancellationToken cancellationToken = default) =>
        writes.DeleteStashAsync(repositoryPath, index, cancellationToken);

    public Task<GitOperationResult> AddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken = default) =>
        remotes.AddRemoteAsync(repositoryPath, name, url, cancellationToken);

    public Task<GitOperationResult> UpdateRemoteAsync(
        string repositoryPath,
        string currentName,
        string newName,
        string url,
        CancellationToken cancellationToken = default) =>
        remotes.UpdateRemoteAsync(repositoryPath, currentName, newName, url, cancellationToken);

    public Task<GitOperationResult> RemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        remotes.RemoveRemoteAsync(repositoryPath, name, cancellationToken);

    public Task<GitOperationResult> FetchAsync(
        string repositoryPath, string remoteName, RemoteCredential? credential = null,
        CancellationToken cancellationToken = default) =>
        remotes.FetchAsync(repositoryPath, remoteName, credential, cancellationToken);

    public Task<GitOperationResult> PullAsync(
        string repositoryPath,
        string remoteName,
        string remoteBranchName,
        PullStrategy strategy,
        RemoteCredential? credential = null,
        GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        remotes.PullAsync(repositoryPath, remoteName, remoteBranchName, strategy, credential, identity, cancellationToken);

    public Task<GitOperationResult> PushAsync(
        string repositoryPath,
        string remoteName,
        bool forceWithLease,
        RemoteCredential? credential = null,
        IProgress<GitPushProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        remotes.PushAsync(repositoryPath, remoteName, forceWithLease, credential, progress, cancellationToken);

    public Task<IReadOnlyList<ConflictFile>> GetConflictMetadataAsync(string repositoryPath, CancellationToken token) =>
        queries.GetConflictMetadataAsync(repositoryPath, token);

    public Task<ConflictFile> GetConflictAsync(string repositoryPath, string path, CancellationToken token) =>
        queries.GetConflictAsync(repositoryPath, path, token);

    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        queries.GetConflictsAsync(repositoryPath, cancellationToken);

    public Task<GitOperationResult> ResolveConflictAsync(
        string repositoryPath, string path, string resultText,
        CancellationToken cancellationToken = default, TextDocument? originalDocument = null) =>
        writes.ResolveConflictAsync(repositoryPath, path, resultText, cancellationToken, originalDocument);

    public Task<GitOperationResult> ResolveBinaryConflictAsync(
        string repositoryPath, string path, ConflictSide side,
        CancellationToken cancellationToken = default) =>
        writes.ResolveBinaryConflictAsync(repositoryPath, path, side, cancellationToken);

    public Task<GitOperationResult> ContinueOperationAsync(
        string repositoryPath, GitIdentity? identity = null,
        CancellationToken cancellationToken = default) =>
        writes.ContinueOperationAsync(repositoryPath, identity, cancellationToken);

    public Task<GitOperationResult> AbortOperationAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        writes.AbortOperationAsync(repositoryPath, cancellationToken);

    public GitOperationPreview Preview(string operation, params string[] affectedItems) =>
        writes.Preview(operation, affectedItems);

    public Task ExportCommitFileAsync(string repositoryPath, string commitId, string path,
        string destination, CancellationToken token) =>
        queries.ExportCommitFileAsync(repositoryPath, commitId, path, destination, token);
}
