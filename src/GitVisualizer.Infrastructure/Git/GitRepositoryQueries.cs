using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

using static GitVisualizer.Infrastructure.Git.GitRepositorySupport;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class GitRepositoryQueries(GitOperationCoordinator operations)
{
    public Task<bool> IsRepositoryAsync(
        string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Directory.Exists(path) && Repository.IsValid(Path.GetFullPath(path));
        }, cancellationToken);

    public Task<GitIdentity?> GetIdentityAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var name = repository.Config.Get<string>("user.name")?.Value;
            var email = repository.Config.Get<string>("user.email")?.Value;
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
                ? null
                : new GitIdentity(name, email);
        }, cancellationToken);

    public Task<GitIdentity?> GetDefaultIdentityAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var configuration = Configuration.BuildFrom(null!);
            var name = configuration.Get<string>("user.name")?.Value;
            var email = configuration.Get<string>("user.email")?.Value;
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
                ? null
                : new GitIdentity(name, email);
        }, cancellationToken);

    public Task<RepositorySnapshot> GetSnapshotAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        GetSnapshotAsync(repositoryPath, null, RepositoryChangeKind.All, cancellationToken);

    public Task<RepositorySnapshot> GetSnapshotAsync(string repositoryPath, RepositorySnapshot? previous,
        RepositoryChangeKind changesToLoad, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var reloadReferences = previous is null || (changesToLoad &
                (RepositoryChangeKind.References | RepositoryChangeKind.Configuration)) != 0;
            var status = repository.RetrieveStatus(new StatusOptions
            {
                IncludeIgnored = true,
                IncludeUntracked = true,
                RecurseIgnoredDirs = false,
                RecurseUntrackedDirs = true,
                DetectRenamesInIndex = true,
                DetectRenamesInWorkDir = true
            });
            var changes = status
                .Where(entry =>
                    !GitServiceSupport.IsTransientOfficeLockFile(entry) &&
                    !entry.State.HasFlag(FileStatus.Conflicted))
                .SelectMany(entry =>
                {
                    var fullPath = Path.Combine(repository.Info.WorkingDirectory, entry.FilePath);
                    var info = new FileInfo(fullPath);
                    var size = info.Exists ? info.Length : 0;
                    var isBinary = info.Exists && IsBinary(fullPath);
                    var entries = new List<FileChange>(2);
                    if (GitServiceSupport.HasStagedChanges(entry.State))
                    {
                        entries.Add(new FileChange(
                            entry.FilePath,
                            null,
                            GitServiceSupport.MapStatus(entry.State, staged: true),
                            true,
                            size,
                            isBinary));
                    }
                    if (GitServiceSupport.HasUnstagedChanges(entry.State))
                    {
                        entries.Add(new FileChange(
                            entry.FilePath,
                            null,
                            GitServiceSupport.MapStatus(entry.State, staged: false),
                            false,
                            size,
                            isBinary));
                    }
                    return entries;
                }).ToArray();

            var branches = !reloadReferences ? previous!.Branches : repository.Branches.Select(branch =>
            {
                var trackedBranch = branch.TrackedBranch;
                var divergence = branch.Tip is null || trackedBranch?.Tip is null
                    ? null
                    : repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, trackedBranch.Tip);
                return new BranchInfo(
                    branch.FriendlyName,
                    branch.CanonicalName,
                    branch.Tip?.Id.Sha ?? string.Empty,
                    branch.IsCurrentRepositoryHead,
                    branch.IsRemote,
                    trackedBranch?.FriendlyName,
                    divergence?.AheadBy ?? 0,
                    divergence?.BehindBy ?? 0);
            }).OrderByDescending(x => x.IsCurrent).ThenBy(x => x.IsRemote).ThenBy(x => x.FriendlyName).ToArray();

            var tags = !reloadReferences ? previous!.Tags : repository.Tags.Select(tag =>
                    new TagInfo(tag.FriendlyName, tag.PeeledTarget.Id.Sha))
                .OrderBy(x => x.Name)
                .ToArray();
            var remotes = !reloadReferences ? previous!.Remotes : repository.Network.Remotes.Select(remote =>
                new RemoteInfo(
                    remote.Name,
                    remote.Url,
                    remote.PushUrl ?? remote.Url,
                    remote.FetchRefSpecs.Select(x => x.Specification).ToArray(),
                    remote.PushRefSpecs.Select(x => x.Specification).ToArray())).ToArray();

            var features = DetectFeatures(repository);
            var isHeadDetached = repository.Info.IsHeadDetached;
            return new RepositorySnapshot(
                Path.GetFullPath(repositoryPath),
                repository.Info.WorkingDirectory,
                new HeadInfo(
                    repository.Head.Tip?.Id.Sha ?? string.Empty,
                    isHeadDetached ? null : repository.Head.FriendlyName,
                    isHeadDetached),
                repository.Info.IsBare,
                MapOperation(repository.Info.CurrentOperation),
                changes,
                branches,
                tags,
                remotes,
                features,
                DateTimeOffset.Now);
        }, cancellationToken);

    private readonly object eventCacheGate = new();

    private string? eventCacheKey;

    private GitHistoryEvent[] cachedGitEvents = [];

    public async Task<IReadOnlyList<GitHistoryEvent>> GetHistoryEventsAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var events = await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var repository = new Repository(repositoryPath);
                var key = EventFingerprint(repository);
                lock (eventCacheGate)
                {
                    if (eventCacheKey != key)
                    {
                        var fresh = ReadGitHistoryEvents(repository, cancellationToken).ToArray();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (EventFingerprint(repository) == key)
                        { cachedGitEvents = fresh; eventCacheKey = key; }
                        else return fresh.ToList();
                    }
                    // Database-derived events are merged into a private list, never into the cached array.
                    return cachedGitEvents.ToList();
                }
            },
            cancellationToken).ConfigureAwait(false);

        var operationEntries = await operations.operationLog.GetRecentAsync(
                Path.GetFullPath(repositoryPath),
                1000,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var entry in operationEntries.Where(entry => entry.Success))
        {
            var details = entry.Details ?? [];
            GitHistoryEvent? operationEvent = entry.Operation switch
            {
                "commit" or "amend" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:commit:{entry.Id}",
                        GitHistoryEventKind.CommitCreated,
                        details[0],
                        null,
                        details[1],
                        $"该提交由 {details[1]} 分支产生",
                        entry.Timestamp),
                "branch-create" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:branch-create:{entry.Id}",
                        GitHistoryEventKind.BranchCreated,
                        details[1],
                        null,
                        details[0],
                        $"分支 {details[0]} 从此提交创建",
                        entry.Timestamp),
                "branch-delete" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:branch-delete:{entry.Id}",
                        GitHistoryEventKind.BranchDeleted,
                        details[1],
                        null,
                        details[0],
                        $"分支 {details[0]} 已删除；提交历史仍然保留",
                        entry.Timestamp),
                "branch-checkout" when HasDetails(details, 3) =>
                    new GitHistoryEvent(
                        $"operation:checkout:{entry.Id}",
                        GitHistoryEventKind.Checkout,
                        details[2],
                        details[1],
                        details[0],
                        $"checkout 将 HEAD 从 {ShortId(details[1])} 移动到分支 {details[0]}（{ShortId(details[2])}）",
                        entry.Timestamp),
                "branch-checkout" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:checkout:{entry.Id}",
                        GitHistoryEventKind.Checkout,
                        details[1],
                        null,
                        details[0],
                        $"checkout 将 HEAD 移动到分支 {details[0]}",
                        entry.Timestamp),
                "commit-checkout" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:checkout:{entry.Id}",
                        GitHistoryEventKind.Checkout,
                        details[1],
                        details[0],
                        null,
                        $"checkout 将 HEAD 从 {ShortId(details[0])} 移动到提交 {ShortId(details[1])}（Detached HEAD）",
                        entry.Timestamp),
                "reset" when details.Count >= 4 &&
                                  !string.IsNullOrWhiteSpace(details[1]) &&
                                  !string.IsNullOrWhiteSpace(details[2]) =>
                    new GitHistoryEvent(
                        $"operation:reset:{entry.Id}",
                        GitHistoryEventKind.Reset,
                        details[2],
                        details[1],
                        string.IsNullOrWhiteSpace(details[0]) ? null : details[0],
                        string.IsNullOrWhiteSpace(details[0])
                            ? $"reset 将 HEAD 从 {ShortId(details[1])} 移动到 {ShortId(details[2])}"
                            : $"reset 将分支 {details[0]} 从 {ShortId(details[1])} 移动到 {ShortId(details[2])}",
                        entry.Timestamp),
                "reset" when HasDetails(details, 1) =>
                    new GitHistoryEvent(
                        $"operation:reset:{entry.Id}",
                        GitHistoryEventKind.Reset,
                        details[0],
                        null,
                        details.Count > 1 ? details[1] : null,
                        $"reset 将指针移动到 {ShortId(details[0])}",
                        entry.Timestamp),
                "merge" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:merge:{entry.Id}",
                        GitHistoryEventKind.Merge,
                        details[0],
                        null,
                        details[1],
                        $"分支 {details[1]} 在此合并",
                        entry.Timestamp),
                "revert" when HasDetails(details, 2) =>
                    new GitHistoryEvent(
                        $"operation:revert:{entry.Id}",
                        GitHistoryEventKind.Revert,
                        details[0],
                        details[1],
                        null,
                        $"该提交用于撤销 {ShortId(details[1])}；原提交历史仍然保留",
                        entry.Timestamp),
                _ => null
            };
            if (operationEvent is not null)
            {
                events.Add(operationEvent);
            }
        }

        return events
            .GroupBy(historyEvent => historyEvent.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(historyEvent => historyEvent.OccurredAt)
            .ToArray();
    }

    public Task<IReadOnlyList<CommitTreeEntry>> GetCommitTreeAsync(
        string repositoryPath,
        string commitId,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CommitTreeEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var commit = repository.Lookup<Commit>(commitId)
                         ?? throw new ArgumentException("提交不存在。", nameof(commitId));
            var entries = new List<CommitTreeEntry>();
            AddTreeEntries(repository, commit.Tree, string.Empty, entries, cancellationToken);
            return entries;
        }, cancellationToken);

    public Task<IReadOnlyList<CommitTreeEntry>> GetCommitDirectoryAsync(string repositoryPath, string commitId,
        string directory, CancellationToken token) => Task.Run<IReadOnlyList<CommitTreeEntry>>(() =>
    {
        using var repository = new Repository(repositoryPath);
        var commit = repository.Lookup<Commit>(commitId) ?? throw new IOException("提交不存在。");
        var tree = string.IsNullOrEmpty(directory) ? commit.Tree : commit.Tree[directory]?.Target as Tree;
        if (tree is null) throw new IOException("历史目录不存在。");
        return tree.Select(entry =>
        {
            token.ThrowIfCancellationRequested();
            var path = string.IsNullOrEmpty(directory) ? entry.Name : directory + "/" + entry.Name;
            // Tree entries already carry type; never load a Blob merely to populate the tree.
            return new CommitTreeEntry(path, entry.TargetType == TreeEntryTargetType.Tree);
        }).ToArray();
    }, token);

    public Task<TextDocument> OpenCommitFileAsync(
        string repositoryPath,
        string commitId,
        string path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var commit = repository.Lookup<Commit>(commitId)
                         ?? throw new ArgumentException("提交不存在。", nameof(commitId));
            var normalizedPath = path.Replace('\\', '/').TrimStart('/');
            var entry = commit.Tree[normalizedPath]
                        ?? throw new FileNotFoundException("该提交中不存在此文件。", normalizedPath);
            if (entry.Target is not Blob blob)
            {
                throw new InvalidOperationException("所选项目不是普通文件。");
            }

            var displayPath =
                $"{Path.GetFullPath(repositoryPath)}@{commit.Id.Sha[..Math.Min(8, commit.Id.Sha.Length)]}:{normalizedPath}";
            var size = repository.ObjectDatabase.RetrieveObjectMetadata(blob.Id).Size;
            if (size > TextFileStorage.EditLimit)
                return TextFileStorage.Oversized(displayPath, size, commit.Author.When);
            using var content = blob.GetContentStream();
            var bytes = new byte[checked((int)size)];
            content.ReadExactly(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            return TextFileStorage.Decode(displayPath, bytes, commit.Author.When, readOnly: true)
                with { ContentBytes = bytes };

        }, cancellationToken);

    public Task<GitOperationResult> CheckoutBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        var command = $"git switch {GitServiceSupport.Quote(name)}";
        return operations.ExecuteWriteAsync(repositoryPath, "branch-checkout", command, GitOperationRisk.Caution, false, null,
            repository =>
            {
                EnsureClean(repository);
                var branch = repository.Branches[name] ?? throw new ArgumentException("分支不存在。", nameof(name));
                var oldHeadId = repository.Head.Tip?.Id.Sha ?? string.Empty;
                Commands.Checkout(repository, branch);
                return GitOperationResult.Ok(
                    "branch-checkout",
                    $"已切换到 {branch.FriendlyName}",
                    command,
                    [
                        branch.FriendlyName,
                        oldHeadId,
                        branch.Tip?.Id.Sha ?? string.Empty
                    ]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CheckoutCommitAsync(
        string repositoryPath,
        string commitId,
        CancellationToken cancellationToken = default)
    {
        var command = $"git checkout --detach {commitId}";
        return operations.ExecuteWriteAsync(
            repositoryPath,
            "commit-checkout",
            command,
            GitOperationRisk.Caution,
            false,
            null,
            repository =>
            {
                EnsureClean(repository);
                if (string.IsNullOrWhiteSpace(commitId))
                {
                    throw new ArgumentException("提交 ID 不能为空。", nameof(commitId));
                }
                var commit = repository.Lookup<Commit>(commitId)
                             ?? throw new ArgumentException("提交不存在。", nameof(commitId));
                var oldHeadId = repository.Head.Tip?.Id.Sha ?? string.Empty;
                Commands.Checkout(repository, commit);
                return GitOperationResult.Ok(
                    "commit-checkout",
                    $"HEAD 已切换到 {ShortId(commit.Id.Sha)}（Detached HEAD）",
                    command,
                    [oldHeadId, commit.Id.Sha]);
            },
            cancellationToken: cancellationToken);
    }

    public Task<BranchDeletionCheck> CheckBranchDeletionAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var branch = repository.Branches[name]
                         ?? throw new ArgumentException("分支不存在。", nameof(name));
            var mainline = ResolveMainline(repository);
            var uncommittedChangeCount = repository.RetrieveStatus()
                .Count(GitServiceSupport.IsMeaningfulChange);

            return new BranchDeletionCheck(
                branch.FriendlyName,
                mainline.FriendlyName,
                branch.IsCurrentRepositoryHead,
                branch.IsRemote,
                string.Equals(
                    branch.CanonicalName,
                    mainline.CanonicalName,
                    StringComparison.OrdinalIgnoreCase),
                IsMergedInto(branch, mainline, repository),
                uncommittedChangeCount);
        }, cancellationToken);

    public Task<IReadOnlyList<StashInfo>> GetStashesAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<StashInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            return repository.Stashes
                .Select((stash, index) => new StashInfo(
                    index,
                    NormalizeStashMessage(stash.Message),
                    stash.WorkTree.Id.Sha,
                    stash.Base.Id.Sha,
                    stash.WorkTree.Committer.When))
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<ConflictFile>> GetConflictMetadataAsync(string repositoryPath, CancellationToken token) =>
        Task.Run<IReadOnlyList<ConflictFile>>(() =>
        {
            using var repository = new Repository(repositoryPath);
            return repository.Index.Conflicts.Select(conflict =>
            {
                token.ThrowIfCancellationRequested();
                var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? string.Empty;
                return new ConflictFile(path, "", "", "", "", false, false, IsLoaded: false);
            }).ToArray();
        }, token);

    public async Task<ConflictFile> GetConflictAsync(string repositoryPath, string path, CancellationToken token) =>
        (await ReadConflictsAsync(repositoryPath, path, token).ConfigureAwait(false)).Single();

    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ReadConflictsAsync(repositoryPath, null, cancellationToken);

    private Task<IReadOnlyList<ConflictFile>> ReadConflictsAsync(
        string repositoryPath, string? selectedPath, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ConflictFile>>(() =>
        {
            using var repository = new Repository(repositoryPath);
            if (!File.Exists(Path.Combine(repository.Info.Path, "index"))) return Array.Empty<ConflictFile>();
            // Hash before LibGit2Sharp first loads/caches the conflict index.
            var indexDigest = ConflictIndexDigest(repository);
            var result = repository.Index.Conflicts.Where(conflict => selectedPath is null ||
                (conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path) == selectedPath).Select(conflict =>
            {
                var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? string.Empty;
                var fullPath = Path.Combine(repository.Info.WorkingDirectory, path);
                var original = File.Exists(fullPath)
                    ? TextFileStorage.OpenAsync(fullPath, cancellationToken).GetAwaiter().GetResult()
                    : null;
                var documents = new Dictionary<ObjectId, TextDocument>();
                TextDocument Version(IndexEntry? entry)
                {
                    if (entry is null) return TextFileStorage.Decode(path, []);
                    if (!documents.TryGetValue(entry.Id, out var document))
                        documents[entry.Id] = document = ReadBlobDocument(repository, path, entry);
                    return document;
                }
                var ancestor = Version(conflict.Ancestor);
                var ours = Version(conflict.Ours);
                var theirs = Version(conflict.Theirs);
                var blocked = original is null || original.IsReadOnly || ancestor.IsReadOnly ||
                    ours.IsReadOnly || theirs.IsReadOnly;
                if (blocked && original is not null)
                    original = original with { IsReadOnly = true, ReadOnlyReason =
                        original.ReadOnlyReason ?? "冲突版本无法安全文本编辑，请使用外部程序处理。" };
                if (original is not null) original = original with { ConflictIndexDigest = indexDigest };
                return new ConflictFile(path, ancestor.Text, ours.Text, theirs.Text,
                    original?.Text ?? string.Empty,
                    ancestor.IsBinary || ours.IsBinary || theirs.IsBinary || original?.IsBinary == true,
                    false, original);

            }).ToArray();
            if (ConflictIndexDigest(repository) != indexDigest)
                throw new IOException("加载冲突时暂存区发生变化，请刷新。 ");
            return result;
        }, cancellationToken);

    public Task ExportCommitFileAsync(string repositoryPath, string commitId, string path,
        string destination, CancellationToken token) => Task.Run(async () =>
    {
        using var repository = new Repository(repositoryPath);
        var commit = repository.Lookup<Commit>(commitId) ?? throw new IOException("提交不存在。");
        var blob = commit.Tree[path]?.Target as Blob ?? throw new IOException("历史文件不存在。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var size = repository.ObjectDatabase.RetrieveObjectMetadata(blob.Id).Size;
                if (size > TextFileStorage.EditLimit)
                    await CopyLargeBlobAsync(repositoryPath, blob.Id.Sha, output, token).ConfigureAwait(false);
                else
                {
                    using var input = blob.GetContentStream();
                    await input.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
                }
                if (output.Length != size) throw new IOException("历史文件导出长度不一致。");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
}
