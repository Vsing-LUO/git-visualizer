namespace GitVisualizer.Core;

public interface IGitRepositoryService
{
    Task<bool> IsRepositoryAsync(
        string path, CancellationToken cancellationToken = default);
    Task<GitIdentity?> GetIdentityAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    Task<GitOperationResult> SetIdentityAsync(
        string repositoryPath, GitIdentity identity, bool global,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> InitializeAsync(
        string path, GitIdentity identity, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CloneAsync(
        string url, string path, RemoteCredential? credential = null,
        CancellationToken cancellationToken = default);
    Task<RepositorySnapshot> GetSnapshotAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommitNode>> GetHistoryAsync(
        string repositoryPath, int skip, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommitNode>> GetBranchHistoryAsync(
        string repositoryPath, string branchName, int skip, int take,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitHistoryEvent>> GetHistoryEventsAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommitTreeEntry>> GetCommitTreeAsync(
        string repositoryPath, string commitId, CancellationToken cancellationToken = default);
    Task<TextDocument> OpenCommitFileAsync(
        string repositoryPath, string commitId, string path,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> StageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UnstageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DiscardFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CommitAsync(
        string repositoryPath, string message, GitIdentity? identity = null, bool amend = false,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateBranchAsync(
        string repositoryPath, string name, string? startPoint = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> CheckoutCommitAsync(
        string repositoryPath, string commitId, CancellationToken cancellationToken = default);
    Task<GitOperationResult> RenameBranchAsync(
        string repositoryPath, string oldName, string newName,
        CancellationToken cancellationToken = default);
    Task<BranchDeletionCheck> CheckBranchDeletionAsync(
        string repositoryPath, string name,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteBranchAsync(
        string repositoryPath, string name, bool force,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> MergeAsync(
        string repositoryPath, string branchName, GitIdentity? identity = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> CherryPickAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> RevertAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> RebaseOntoAsync(
        string repositoryPath, string upstreamBranch, string? ontoBranch = null,
        GitIdentity? identity = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResetAsync(
        string repositoryPath, string targetId, GitResetMode mode,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> CreateTagAsync(
        string repositoryPath, string name, string? targetId = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> SaveStashAsync(
        string repositoryPath, string message, GitIdentity? identity = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StashInfo>> GetStashesAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ApplyStashAsync(
        string repositoryPath, int index, bool pop, CancellationToken cancellationToken = default);
    Task<GitOperationResult> DeleteStashAsync(
        string repositoryPath, int index, CancellationToken cancellationToken = default);
    Task<GitOperationResult> AddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken = default);
    Task<GitOperationResult> UpdateRemoteAsync(
        string repositoryPath, string currentName, string newName, string url,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> RemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default);
    Task<GitOperationResult> FetchAsync(
        string repositoryPath, string remoteName, RemoteCredential? credential = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> PullAsync(
        string repositoryPath, string remoteName, string remoteBranchName,
        PullStrategy strategy, RemoteCredential? credential = null,
        GitIdentity? identity = null, CancellationToken cancellationToken = default);
    Task<GitOperationResult> PushAsync(
        string repositoryPath, string remoteName, bool forceWithLease,
        RemoteCredential? credential = null,
        IProgress<GitPushProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveConflictAsync(
        string repositoryPath, string path, string resultText,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> ResolveBinaryConflictAsync(
        string repositoryPath, string path, ConflictSide side,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> ContinueOperationAsync(
        string repositoryPath, GitIdentity? identity = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> AbortOperationAsync(
        string repositoryPath, CancellationToken cancellationToken = default);
    GitOperationPreview Preview(string operation, params string[] affectedItems);
}

public interface IDiffService
{
    Task<IReadOnlyList<DiffHunk>> GetWorkingDiffAsync(
        string repositoryPath, string path, bool staged,
        CancellationToken cancellationToken = default);
    Task<string> GetUnifiedDiffAsync(
        string repositoryPath, string path, bool staged,
        CancellationToken cancellationToken = default);
    Task<string> CompareCommitsAsync(
        string repositoryPath, string oldCommitId, string newCommitId, string? path = null,
        CancellationToken cancellationToken = default);
}

public interface IIndexPatchService
{
    Task<GitOperationResult> StageHunksAsync(
        string repositoryPath, string path, IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> UnstageHunksAsync(
        string repositoryPath, string path, IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryWatcher : IDisposable
{
    event EventHandler? RepositoryChanged;
    string RepositoryPath { get; }
    void Start();
    void Stop();
}

public interface IRepositoryWatcherFactory
{
    IRepositoryWatcher Create(string repositoryPath);
}

public interface IRecoveryService
{
    Task<RecoveryPoint> CreateAsync(
        string repositoryPath, string operation, IReadOnlyList<string>? affectedPaths = null,
        CancellationToken cancellationToken = default);
    Task<GitOperationResult> RestoreAsync(
        RecoveryPoint point, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecoveryPoint>> ListAsync(
        string? repositoryPath = null, CancellationToken cancellationToken = default);
    Task PruneAsync(CancellationToken cancellationToken = default);
}

public interface ICredentialVault
{
    Task SaveAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IOperationLogStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AddAsync(OperationLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(
        string? repositoryPath, int count, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IFileWorkspaceService
{
    Task<TextDocument> OpenTextAsync(string path, CancellationToken cancellationToken = default);
    Task OpenExternalAsync(string path, CancellationToken cancellationToken = default);
    Task SaveTextAsync(
        TextDocument original, string text, bool allowExternalOverwrite,
        CancellationToken cancellationToken = default);
    Task CreateFileAsync(string path, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

public interface ISystemNewFileService
{
    Task<IReadOnlyList<SystemNewFileType>> GetAvailableTypesAsync(
        CancellationToken cancellationToken = default);
    Task CreateAsync(
        string path,
        string typeId,
        CancellationToken cancellationToken = default);
}
