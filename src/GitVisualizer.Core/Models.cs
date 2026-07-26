namespace GitVisualizer.Core;

public enum GitChangeState
{
    Unmodified,
    Added,
    Modified,
    Deleted,
    Renamed,
    Conflicted,
    Ignored,
    Untracked
}

public enum GitOperationRisk
{
    Safe,
    Caution,
    Dangerous
}

public enum RepositoryOperationState
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Bisect,
    Unknown
}

public enum PullStrategy
{
    Ask,
    Merge,
    Rebase,
    FastForwardOnly
}

public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
}

public enum CredentialKind
{
    None,
    HttpsToken,
    SshKey,
    SshAgent
}

public enum ConflictSide
{
    Ours,
    Theirs,
    Both,
    Manual
}

public sealed record GitIdentity(string Name, string Email);

public sealed record RemoteCredential(
    CredentialKind Kind,
    string UserName = "",
    string Secret = "",
    string PrivateKeyPath = "",
    string PublicKeyPath = "",
    bool Remember = false);

public sealed record FileChange(
    string Path,
    string? OldPath,
    GitChangeState State,
    bool IsStaged,
    long Size = 0,
    bool IsBinary = false);

public sealed record DiffLine(char Origin, int? OldLine, int? NewLine, string Text);

public sealed record DiffHunk(
    string Id,
    string Path,
    string Header,
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    IReadOnlyList<DiffLine> Lines,
    string Patch,
    string SnapshotId,
    bool IsStaged);

public sealed record CommitNode(
    string Id,
    string ShortId,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    IReadOnlyList<string> ParentIds,
    IReadOnlyList<string> Decorations,
    int Lane = 0);

public sealed record BranchInfo(
    string FriendlyName,
    string CanonicalName,
    string TipId,
    bool IsCurrent,
    bool IsRemote,
    string? TrackedBranch,
    int AheadBy,
    int BehindBy);

public sealed record TagInfo(string Name, string TargetId);

public sealed record RemoteInfo(
    string Name,
    string FetchUrl,
    string PushUrl,
    IReadOnlyList<string> FetchRefSpecs,
    IReadOnlyList<string> PushRefSpecs);

public sealed record RepositoryFeatures(
    bool HasGitLfs,
    bool HasSubmodules,
    bool HasHooks,
    IReadOnlyList<string> Notices);

public sealed record RepositorySnapshot(
    string RepositoryPath,
    string WorkingDirectory,
    string HeadId,
    string CurrentBranch,
    bool IsBare,
    bool IsHeadDetached,
    RepositoryOperationState OperationState,
    IReadOnlyList<FileChange> Changes,
    IReadOnlyList<BranchInfo> Branches,
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<RemoteInfo> Remotes,
    RepositoryFeatures Features,
    DateTimeOffset RefreshedAt);

public sealed record GitOperationPreview(
    string Operation,
    string Description,
    string EquivalentCommand,
    GitOperationRisk Risk,
    IReadOnlyList<string> AffectedItems,
    bool CreatesRecoveryPoint,
    string RecoveryDescription);

public sealed record GitOperationResult(
    bool Success,
    string Operation,
    string Summary,
    string EquivalentCommand,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Warnings,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? RecoveryPointId = null)
{
    public static GitOperationResult Ok(
        string operation,
        string summary,
        string equivalentCommand,
        IEnumerable<string>? details = null,
        IEnumerable<string>? warnings = null,
        string? recoveryPointId = null) =>
        new(true, operation, summary, equivalentCommand,
            details?.ToArray() ?? [], warnings?.ToArray() ?? [],
            RecoveryPointId: recoveryPointId);

    public static GitOperationResult Fail(
        string operation,
        string equivalentCommand,
        Exception exception,
        string? errorCode = null) =>
        new(false, operation, "操作失败", equivalentCommand, [], [],
            errorCode ?? exception.GetType().Name, exception.Message);
}

public sealed record ConflictFile(
    string Path,
    string BaseText,
    string OursText,
    string TheirsText,
    string ResultText,
    bool IsBinary,
    bool IsResolved);

public sealed record RecoveryPoint(
    string Id,
    string RepositoryPath,
    string Operation,
    string HeadId,
    string ReferenceName,
    string ArchivePath,
    DateTimeOffset CreatedAt,
    long Size,
    bool IsRestorable);

public sealed record OperationLogEntry(
    string Id,
    DateTimeOffset Timestamp,
    string RepositoryPath,
    string Operation,
    bool Success,
    GitOperationRisk Risk,
    string Summary,
    string EquivalentCommand,
    string? RecoveryPointId,
    string? ErrorCode);

public sealed record AppSettings(
    IReadOnlyList<string> RecentRepositories,
    string Theme,
    bool AutoSave,
    string? LastRepository,
    IReadOnlyDictionary<string, PullStrategy> PullStrategies)
{
    public static AppSettings Default { get; } =
        new([], "System", false, null, new Dictionary<string, PullStrategy>());
}

public sealed record TextDocument(
    string Path,
    string Text,
    string EncodingName,
    string NewLine,
    DateTimeOffset LastWriteTime,
    bool IsReadOnly,
    bool IsBinary,
    long Size);
