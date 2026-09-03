using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitRepositoryService : IGitRepositoryService
{
    private static readonly Regex ReflogLinePattern = new(
        """^(?<old>[0-9a-fA-F]+) (?<new>[0-9a-fA-F]+) .+ <[^>]*> (?<time>\d+) [+-]\d{4}\t(?<message>.*)$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RevertedCommitPattern = new(
        """This reverts commit (?<id>[0-9a-fA-F]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private readonly IRecoveryService recoveryService;
    private readonly IOperationLogStore operationLog;

    public LibGitRepositoryService(IRecoveryService recoveryService, IOperationLogStore operationLog)
    {
        this.recoveryService = recoveryService;
        this.operationLog = operationLog;
    }

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

    public Task<GitOperationResult> SetGlobalIdentityAsync(
        GitIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateIdentity(identity);
            try
            {
                using var configuration = Configuration.BuildFrom(null!);
                configuration.Set("user.name", identity.Name.Trim(), ConfigurationLevel.Global);
                configuration.Set("user.email", identity.Email.Trim(), ConfigurationLevel.Global);
                return GitOperationResult.Ok(
                    "identity-config", "已更新全局 Git 身份",
                    "git config --global user.name <name>",
                    [$"{identity.Name.Trim()} <{identity.Email.Trim()}>"]);
            }
            catch (Exception exception)
            {
                return GitOperationResult.Fail(
                    "identity-config", "git config --global user.name <name>", exception);
            }
        }, cancellationToken);

    public Task<GitOperationResult> SetIdentityAsync(
        string repositoryPath,
        GitIdentity identity,
        bool global,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        var level = global ? ConfigurationLevel.Global : ConfigurationLevel.Local;
        return ExecuteWriteAsync(
            repositoryPath,
            "identity-config",
            $"git config {(global ? "--global " : string.Empty)}user.name <name>",
            GitOperationRisk.Safe,
            false,
            null,
            repository =>
            {
                repository.Config.Set("user.name", identity.Name.Trim(), level);
                repository.Config.Set("user.email", identity.Email.Trim(), level);
                return GitOperationResult.Ok(
                    "identity-config",
                    global ? "默认 Git 身份已更新" : "仓库 Git 身份已更新",
                    $"git config {(global ? "--global " : string.Empty)}user.name <name>",
                    [$"{identity.Name} <{identity.Email}>"]);
            },
            cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> InitializeAsync(
        string path, GitIdentity? identity = null, CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(path, "init", "git init", GitOperationRisk.Safe, false, null, repository =>
        {
            if (identity is not null)
            {
                ValidateIdentity(identity);
                repository.Config.Set("user.name", identity.Name.Trim(), ConfigurationLevel.Local);
                repository.Config.Set("user.email", identity.Email.Trim(), ConfigurationLevel.Local);
            }
            return GitOperationResult.Ok("init", "仓库初始化完成", "git init", [path]);
        }, initializeIfNeeded: true, cancellationToken);

    public async Task<GitOperationResult> CloneAsync(
        string url,
        string path,
        RemoteCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        const string command = "git clone <remote-url> <folder>";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedUrl = NormalizeRemoteAddress(url);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new IOException("克隆目标文件夹必须为空。");
            }

            await Task.Run(
                () => Repository.Clone(
                    normalizedUrl,
                    path,
                    GitServiceSupport.CloneOptions(normalizedUrl, credential)),
                cancellationToken).ConfigureAwait(false);
            var result = GitOperationResult.Ok("clone", "远程仓库克隆完成", command, [path]);
            await LogAsync(path, result, GitOperationRisk.Safe, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var result = GitOperationResult.Fail("clone", command, exception);
            await LogAsync(path, result, GitOperationRisk.Safe, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    public Task<RepositorySnapshot> GetSnapshotAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
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

            var branches = repository.Branches.Select(branch =>
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

            var tags = repository.Tags.Select(tag =>
                    new TagInfo(tag.FriendlyName, tag.PeeledTarget.Id.Sha))
                .OrderBy(x => x.Name)
                .ToArray();
            var remotes = repository.Network.Remotes.Select(remote =>
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

    public Task<IReadOnlyList<CommitNode>> GetHistoryAsync(
        string repositoryPath, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CommitNode>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var historyRoots = ReadHistoryRoots(repository);
            var commits = historyRoots.Count == 0
                ? repository.Commits
                : repository.Commits.QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = historyRoots,
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                });
            return commits
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 1000))
                .Select(MapCommit)
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<CommitNode>> GetBranchHistoryAsync(
        string repositoryPath,
        string branchName,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CommitNode>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var repository = new Repository(repositoryPath);
            var branch = repository.Branches[branchName]
                         ?? throw new ArgumentException("分支不存在。", nameof(branchName));
            return repository.Commits.QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = branch,
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                })
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 1000))
                .Select(MapCommit)
                .ToArray();
        }, cancellationToken);

    public async Task<IReadOnlyList<GitHistoryEvent>> GetHistoryEventsAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var events = await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var repository = new Repository(repositoryPath);
                return ReadGitHistoryEvents(repository, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);

        var operationEntries = await operationLog.GetRecentAsync(
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

    private static bool HasDetails(
        IReadOnlyList<string> details,
        int requiredCount) =>
        details.Count >= requiredCount &&
        details.Take(requiredCount).All(detail =>
            !string.IsNullOrWhiteSpace(detail));

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
            AddTreeEntries(commit.Tree, string.Empty, entries, cancellationToken);
            return entries;
        }, cancellationToken);

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

            using var content = blob.GetContentStream();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var isBinary = IsBinary(bytes);
            var encoding = DetectEncoding(bytes, out var preambleLength);
            var text = isBinary
                ? string.Empty
                : encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var displayPath =
                $"{Path.GetFullPath(repositoryPath)}@{commit.Id.Sha[..Math.Min(8, commit.Id.Sha.Length)]}:{normalizedPath}";

            return new TextDocument(
                displayPath,
                text,
                encoding.WebName,
                newLine,
                commit.Author.When,
                true,
                isBinary,
                bytes.LongLength,
                bytes);
        }, cancellationToken);

    public Task<GitOperationResult> StageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git add -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return ExecuteWriteAsync(repositoryPath, "stage", command, GitOperationRisk.Safe, false, paths, repository =>
        {
            Commands.Stage(repository, paths);
            return GitOperationResult.Ok("stage", $"已暂存 {paths.Count} 个文件", command, paths);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> UnstageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git restore --staged -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return ExecuteWriteAsync(repositoryPath, "unstage", command, GitOperationRisk.Safe, false, paths, repository =>
        {
            Commands.Unstage(repository, paths);
            return GitOperationResult.Ok("unstage", $"已取消暂存 {paths.Count} 个文件", command, paths);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DiscardFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git restore -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return ExecuteWriteAsync(repositoryPath, "discard", command, GitOperationRisk.Dangerous, true, paths, repository =>
        {
            if (repository.Info.IsBare)
            {
                throw new InvalidOperationException("裸仓库没有可丢弃的工作区修改。");
            }

            var normalizedPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => NormalizeWorkTreePath(repository, path))
                .DistinctBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedPaths.Length == 0)
            {
                throw new ArgumentException("请至少选择一个要丢弃修改的文件。", nameof(paths));
            }

            var conflictPaths = repository.Index.Conflicts
                .Select(conflict => conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requestedConflicts = normalizedPaths
                .Select(item => item.RelativePath)
                .Where(conflictPaths.Contains)
                .ToArray();
            if (requestedConflicts.Length > 0)
            {
                throw new InvalidOperationException(
                    "冲突文件不能按普通未暂存修改丢弃：" +
                    string.Join("、", requestedConflicts) +
                    "。请在冲突解决器中采用当前/对方版本，或中止当前 Git 操作。");
            }

            foreach (var (relativePath, fullPath) in normalizedPaths)
            {
                var indexEntry = repository.Index[relativePath];
                if (indexEntry is null)
                {
                    DeleteWorkTreeFile(fullPath);
                    continue;
                }

                if (indexEntry.Mode == Mode.GitLink)
                {
                    throw new InvalidOperationException($"子模块路径不能作为普通文件丢弃：{relativePath}");
                }

                var blob = repository.Lookup<Blob>(indexEntry.Id)
                           ?? throw new InvalidOperationException($"无法读取暂存区中的文件内容：{relativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                PrepareFileForOverwrite(fullPath);
                using var source = blob.GetContentStream(new FilteringOptions(relativePath));
                using var destination = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                source.CopyTo(destination);
            }

            var discardedPaths = normalizedPaths.Select(item => item.RelativePath).ToArray();
            return GitOperationResult.Ok(
                "discard",
                $"已丢弃 {discardedPaths.Length} 个文件的未暂存修改",
                command,
                discardedPaths,
                ["已暂存内容保持不变；操作前状态已保存在恢复中心。"]);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CommitAsync(
        string repositoryPath,
        string message,
        GitIdentity? identity = null,
        bool amend = false,
        CancellationToken cancellationToken = default)
    {
        var command = amend ? "git commit --amend" : "git commit";
        return ExecuteWriteAsync(repositoryPath, amend ? "amend" : "commit", command,
            amend ? GitOperationRisk.Dangerous : GitOperationRisk.Safe, amend, null, repository =>
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    throw new ArgumentException("提交说明不能为空。", nameof(message));
                }
                var signature = GitServiceSupport.ResolveSignature(repository, identity);
                var commit = repository.Commit(message.Trim(), signature, signature,
                    new CommitOptions { AmendPreviousCommit = amend });
                return GitOperationResult.Ok(
                    amend ? "amend" : "commit",
                    amend ? "上一提交已修改" : "提交创建成功",
                    command,
                    [commit.Id.Sha, repository.Head.FriendlyName, commit.MessageShort]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CreateBranchAsync(
        string repositoryPath, string name, string? startPoint = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git branch {GitServiceSupport.Quote(name)} {startPoint ?? string.Empty}".TrimEnd();
        return ExecuteWriteAsync(repositoryPath, "branch-create", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                var target = startPoint is null
                    ? repository.Head.Tip
                    : repository.Lookup<Commit>(startPoint)
                      ?? repository.Branches[startPoint]?.Tip
                      ?? throw new ArgumentException("找不到分支起点。", nameof(startPoint));
                var branch = repository.CreateBranch(name, target);
                return GitOperationResult.Ok(
                    "branch-create",
                    $"已创建分支 {branch.FriendlyName}",
                    command,
                    [branch.FriendlyName, branch.Tip?.Id.Sha ?? string.Empty]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CheckoutBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        var command = $"git switch {GitServiceSupport.Quote(name)}";
        return ExecuteWriteAsync(repositoryPath, "branch-checkout", command, GitOperationRisk.Caution, false, null,
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
        return ExecuteWriteAsync(
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

    public Task<GitOperationResult> RenameBranchAsync(
        string repositoryPath, string oldName, string newName,
        CancellationToken cancellationToken = default)
    {
        oldName = oldName.Trim();
        newName = newName.Trim();
        var command = $"git branch -m {GitServiceSupport.Quote(oldName)} {GitServiceSupport.Quote(newName)}";
        return ExecuteWriteAsync(repositoryPath, "branch-rename", command, GitOperationRisk.Caution, false, null,
            repository =>
            {
                if (string.IsNullOrWhiteSpace(oldName))
                {
                    throw new ArgumentException("原分支名不能为空。", nameof(oldName));
                }
                if (string.IsNullOrWhiteSpace(newName) ||
                    !Reference.IsValidName($"refs/heads/{newName}"))
                {
                    throw new ArgumentException("新分支名不符合 Git 引用命名规则。", nameof(newName));
                }

                var branch = repository.Branches[oldName]
                             ?? throw new ArgumentException("分支不存在。", nameof(oldName));
                if (branch.IsRemote)
                {
                    throw new InvalidOperationException("不能重命名远程跟踪分支。");
                }
                if (string.Equals(oldName, newName, StringComparison.Ordinal))
                {
                    throw new ArgumentException("新分支名必须与原分支名不同。", nameof(newName));
                }
                if (repository.Branches[newName] is not null)
                {
                    throw new InvalidOperationException($"分支 {newName} 已存在。");
                }

                var tipId = branch.Tip?.Id.Sha ?? string.Empty;
                var renamed = repository.Branches.Rename(branch, newName);
                return GitOperationResult.Ok(
                    "branch-rename",
                    $"分支 {oldName} 已重命名为 {renamed.FriendlyName}",
                    command,
                    [oldName, renamed.FriendlyName, tipId]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DeleteBranchAsync(
        string repositoryPath, string name, bool force, CancellationToken cancellationToken = default)
    {
        var command = $"git branch {(force ? "-D" : "-d")} {GitServiceSupport.Quote(name)}";
        return ExecuteWriteAsync(repositoryPath, "branch-delete", command,
            force ? GitOperationRisk.Dangerous : GitOperationRisk.Caution, force, null, repository =>
            {
                EnsureClean(repository);
                var branch = repository.Branches[name] ?? throw new ArgumentException("分支不存在。");
                if (branch.IsRemote)
                {
                    throw new InvalidOperationException("不能删除远程跟踪分支。");
                }
                if (branch.IsCurrentRepositoryHead)
                {
                    throw new InvalidOperationException("不能删除当前分支，请先切换到其他本地分支。");
                }
                var mainline = ResolveMainline(repository);
                if (string.Equals(
                        branch.CanonicalName,
                        mainline.CanonicalName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"不能删除主线分支 {mainline.FriendlyName}。");
                }
                if (!force && !IsMergedInto(branch, mainline, repository))
                {
                    throw new InvalidOperationException(
                        $"分支尚未合并到主线 {mainline.FriendlyName}，请确认风险后再强制删除。");
                }
                var deletedTipId = branch.Tip?.Id.Sha ?? string.Empty;
                repository.Branches.Remove(branch);
                return GitOperationResult.Ok(
                    "branch-delete",
                    $"已删除分支 {name}",
                    command,
                    [name, deletedTipId]);
            }, cancellationToken: cancellationToken);
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

    public Task<GitOperationResult> MergeAsync(
        string repositoryPath, string branchName, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git merge {GitServiceSupport.Quote(branchName)}";
        return ExecuteWriteAsync(repositoryPath, "merge", command, GitOperationRisk.Caution, true, null, repository =>
        {
            EnsureClean(repository);
            var branch = repository.Branches[branchName] ?? throw new ArgumentException("分支不存在。");
            var result = repository.Merge(branch, GitServiceSupport.ResolveSignature(repository, identity));
            return GitOperationResult.Ok(
                "merge",
                result.Status == MergeStatus.Conflicts ? "合并产生冲突，请在冲突解决器中处理" : "分支合并完成",
                command,
                [
                    repository.Head.Tip?.Id.Sha ?? string.Empty,
                    branchName,
                    $"状态：{result.Status}"
                ],
                result.Status == MergeStatus.Conflicts ? ["存在未解决冲突"] : []);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CherryPickAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git cherry-pick {commitId}";
        return ExecuteWriteAsync(repositoryPath, "cherry-pick", command, GitOperationRisk.Caution, true, null,
            repository =>
            {
                EnsureClean(repository);
                var commit = repository.Lookup<Commit>(commitId)
                             ?? throw new ArgumentException("提交不存在。");
                var result = repository.CherryPick(commit, GitServiceSupport.ResolveSignature(repository, identity));
                return GitOperationResult.Ok("cherry-pick",
                    result.Status == CherryPickStatus.Conflicts ? "拣选产生冲突" : "提交拣选完成",
                    command, [$"状态：{result.Status}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> RevertAsync(
        string repositoryPath, string commitId, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git revert {commitId}";
        return ExecuteWriteAsync(repositoryPath, "revert", command, GitOperationRisk.Caution, true, null,
            repository =>
            {
                EnsureClean(repository);
                var commit = repository.Lookup<Commit>(commitId)
                             ?? throw new ArgumentException("提交不存在。");
                var result = repository.Revert(commit, GitServiceSupport.ResolveSignature(repository, identity));
                return GitOperationResult.Ok("revert",
                    result.Status == RevertStatus.Conflicts ? "撤销产生冲突" : "已创建撤销提交",
                    command,
                    [
                        repository.Head.Tip?.Id.Sha ?? string.Empty,
                        commitId,
                        $"状态：{result.Status}"
                    ]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> RebaseOntoAsync(
        string repositoryPath,
        string upstreamBranch,
        string? ontoBranch = null,
        GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git rebase {(ontoBranch is null ? string.Empty : $"--onto {ontoBranch} ")}{upstreamBranch}";
        return ExecuteWriteAsync(repositoryPath, "rebase", command, GitOperationRisk.Dangerous, true, null,
            repository =>
            {
                EnsureClean(repository);
                var upstream = repository.Branches[upstreamBranch]
                               ?? throw new ArgumentException("上游分支不存在。");
                var onto = ontoBranch is null
                    ? upstream
                    : repository.Branches[ontoBranch] ?? throw new ArgumentException("目标分支不存在。");
                var signature = GitServiceSupport.ResolveSignature(repository, identity);
                var committer = new Identity(signature.Name, signature.Email);
                var result = repository.Rebase.Start(repository.Head, upstream, onto, committer, new RebaseOptions());
                return GitOperationResult.Ok(
                    "rebase",
                    result.Status == RebaseStatus.Conflicts ? "变基产生冲突" : "变基完成",
                    command,
                    [$"状态：{result.Status}", $"已完成：{result.CompletedStepCount}/{result.TotalStepCount}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> ResetAsync(
        string repositoryPath, string targetId, GitResetMode mode,
        CancellationToken cancellationToken = default)
    {
        var option = mode.ToString().ToLowerInvariant();
        var command = $"git reset --{option} {targetId}";
        var risk = mode == GitResetMode.Hard ? GitOperationRisk.Dangerous : GitOperationRisk.Caution;
        return ExecuteWriteAsync(repositoryPath, "reset", command, risk, true, null, repository =>
        {
            var commit = repository.Lookup<Commit>(targetId)
                         ?? throw new ArgumentException("目标提交不存在。");
            var oldHeadId = repository.Head.Tip?.Id.Sha ?? string.Empty;
            var branchName = repository.Info.IsHeadDetached
                ? null
                : repository.Head.FriendlyName;
            var nativeMode = mode switch
            {
                GitResetMode.Soft => LibGitResetMode.Soft,
                GitResetMode.Mixed => LibGitResetMode.Mixed,
                GitResetMode.Hard => LibGitResetMode.Hard,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            repository.Reset(nativeMode, commit);
            var summary = mode switch
            {
                GitResetMode.Soft => "已回退并保留暂存修改",
                GitResetMode.Mixed => "已回退并保留未暂存修改",
                GitResetMode.Hard => "已彻底回到所选版本",
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            return GitOperationResult.Ok(
                "reset",
                summary,
                command,
                [branchName ?? string.Empty, oldHeadId, targetId, option]);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CreateTagAsync(
        string repositoryPath, string name, string? targetId = null,
        GitTagType tagType = GitTagType.Lightweight, string? message = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMessage = message?.Trim();
        var command = tagType == GitTagType.Annotated
            ? $"git tag -a {GitServiceSupport.Quote(name)} {targetId ?? string.Empty} -m {GitServiceSupport.Quote(normalizedMessage ?? string.Empty)}"
            : $"git tag {GitServiceSupport.Quote(name)} {targetId ?? string.Empty}";
        command = command.Trim();
        return ExecuteWriteAsync(repositoryPath, "tag-create", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                var target = targetId is null ? repository.Head.Tip : repository.Lookup<GitObject>(targetId);
                if (target is null)
                {
                    throw new ArgumentException("标签目标不存在。");
                }

                if (tagType == GitTagType.Annotated)
                {
                    if (string.IsNullOrWhiteSpace(normalizedMessage))
                    {
                        throw new ArgumentException("附注标签的说明不能为空。");
                    }

                    repository.Tags.Add(
                        name,
                        target,
                        GitServiceSupport.ResolveSignature(repository, null),
                        normalizedMessage);
                }
                else
                {
                    repository.Tags.Add(name, target);
                }

                var typeName = tagType == GitTagType.Annotated ? "附注标签" : "轻量标签";
                return GitOperationResult.Ok("tag-create", $"已创建{typeName} {name}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        var command = $"git tag -d {GitServiceSupport.Quote(name)}";
        return ExecuteWriteAsync(repositoryPath, "tag-delete", command, GitOperationRisk.Caution, false, null,
            repository =>
            {
                if (repository.Tags[name] is null)
                {
                    throw new ArgumentException("标签不存在。");
                }
                repository.Tags.Remove(name);
                return GitOperationResult.Ok("tag-delete", $"已删除标签 {name}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> SaveStashAsync(
        string repositoryPath, string message, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        const string command = "git stash push --include-untracked";
        return ExecuteWriteAsync(repositoryPath, "stash-save", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                var index = repository.Stashes.Add(
                    GitServiceSupport.ResolveSignature(repository, identity),
                    string.IsNullOrWhiteSpace(message) ? "Git 可视化临时保存" : message,
                    StashModifiers.IncludeUntracked);
                return GitOperationResult.Ok("stash-save", "工作现场已暂存", command, [$"stash@{{{index}}}"]);
            }, cancellationToken: cancellationToken);
    }

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

    public Task<GitOperationResult> ApplyStashAsync(
        string repositoryPath, int index, bool pop, CancellationToken cancellationToken = default)
    {
        var command = $"git stash {(pop ? "pop" : "apply")} stash@{{{index}}}";
        return ExecuteWriteAsync(repositoryPath, pop ? "stash-pop" : "stash-apply", command,
            GitOperationRisk.Caution, true, null, repository =>
            {
                var backupReference = PreserveStash(repository, index);
                var status = pop ? repository.Stashes.Pop(index) : repository.Stashes.Apply(index);
                return GitOperationResult.Ok(
                    pop ? "stash-pop" : "stash-apply",
                    status == StashApplyStatus.Conflicts ? "恢复现场时产生冲突" : "工作现场已恢复",
                    command, [$"状态：{status}", $"安全引用：{backupReference}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DeleteStashAsync(
        string repositoryPath, int index, CancellationToken cancellationToken = default)
    {
        var command = $"git stash drop stash@{{{index}}}";
        return ExecuteWriteAsync(repositoryPath, "stash-delete", command, GitOperationRisk.Dangerous, true, null,
            repository =>
            {
                var backupReference = PreserveStash(repository, index);
                repository.Stashes.Remove(index);
                return GitOperationResult.Ok(
                    "stash-delete", "临时现场已删除", command,
                    [$"安全引用：{backupReference}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> AddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken = default)
    {
        var command = $"git remote add {GitServiceSupport.Quote(name)} <remote-url>";
        return ExecuteWriteAsync(repositoryPath, "remote-add", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                repository.Network.Remotes.Add(name, NormalizeRemoteAddress(url));
                return GitOperationResult.Ok("remote-add", $"已添加远程 {name}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> UpdateRemoteAsync(
        string repositoryPath,
        string currentName,
        string newName,
        string url,
        CancellationToken cancellationToken = default)
    {
        var rename = !string.Equals(currentName, newName, StringComparison.Ordinal);
        var command = rename
            ? $"git remote rename {GitServiceSupport.Quote(currentName)} {GitServiceSupport.Quote(newName)} && " +
              $"git remote set-url {GitServiceSupport.Quote(newName)} <remote-url>"
            : $"git remote set-url {GitServiceSupport.Quote(currentName)} <remote-url>";
        return ExecuteWriteAsync(
            repositoryPath,
            "remote-update",
            command,
            GitOperationRisk.Safe,
            false,
            null,
            repository =>
            {
                var normalizedUrl = NormalizeRemoteAddress(url);
                if (repository.Network.Remotes[currentName] is null)
                {
                    throw new ArgumentException($"远程 {currentName} 不存在。");
                }
                if (rename && repository.Network.Remotes[newName] is not null)
                {
                    throw new InvalidOperationException($"远程名称 {newName} 已存在。");
                }

                var effectiveName = currentName;
                if (rename)
                {
                    repository.Network.Remotes.Rename(currentName, newName);
                    effectiveName = newName;
                }
                repository.Network.Remotes.Update(
                    effectiveName,
                    updater => updater.Url = normalizedUrl,
                    updater => updater.PushUrl = normalizedUrl);
                return GitOperationResult.Ok(
                    "remote-update",
                    $"已更新远程 {effectiveName}",
                    command);
            },
            cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> RemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        var command = $"git remote remove {GitServiceSupport.Quote(name)}";
        return ExecuteWriteAsync(repositoryPath, "remote-remove", command, GitOperationRisk.Caution, false, null,
            repository =>
            {
                if (repository.Network.Remotes[name] is null)
                {
                    throw new ArgumentException("远程不存在。");
                }
                repository.Network.Remotes.Remove(name);
                return GitOperationResult.Ok("remote-remove", $"已移除远程 {name}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> FetchAsync(
        string repositoryPath, string remoteName, RemoteCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git fetch {GitServiceSupport.Quote(remoteName)} --prune";
        return ExecuteWriteAsync(repositoryPath, "fetch", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                var remote = repository.Network.Remotes[remoteName]
                             ?? throw new ArgumentException("远程不存在。");
                var options = GitServiceSupport.FetchOptions(remote.Url, credential);
                options.Prune = true;
                Commands.Fetch(repository, remote.Name,
                    remote.FetchRefSpecs.Select(x => x.Specification), options, "Git 可视化 fetch");
                return GitOperationResult.Ok("fetch", $"已获取 {remoteName} 的更新", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> PullAsync(
        string repositoryPath,
        string remoteName,
        string remoteBranchName,
        PullStrategy strategy,
        RemoteCredential? credential = null,
        GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = strategy switch
        {
            PullStrategy.Rebase => $"git pull --rebase {GitServiceSupport.Quote(remoteName)} {GitServiceSupport.Quote(remoteBranchName)}",
            PullStrategy.FastForwardOnly => $"git pull --ff-only {GitServiceSupport.Quote(remoteName)} {GitServiceSupport.Quote(remoteBranchName)}",
            _ => $"git pull --no-rebase {GitServiceSupport.Quote(remoteName)} {GitServiceSupport.Quote(remoteBranchName)}"
        };
        return ExecuteWriteAsync(repositoryPath, "pull", command, GitOperationRisk.Caution, true, null,
            repository =>
            {
                EnsureClean(repository);
                if (repository.Info.IsHeadDetached)
                {
                    throw new InvalidOperationException("分离头指针状态下不能拉取，请先切换到本地分支。");
                }
                var remote = repository.Network.Remotes[remoteName]
                             ?? throw new ArgumentException("所选远程不存在。");
                if (string.IsNullOrWhiteSpace(remoteBranchName))
                {
                    throw new ArgumentException("请选择远程分支。");
                }
                var signature = GitServiceSupport.ResolveSignature(repository, identity);
                var fetchOptions = GitServiceSupport.FetchOptions(remote.Url, credential);
                Commands.Fetch(repository, remote.Name,
                    remote.FetchRefSpecs.Select(x => x.Specification),
                    fetchOptions, "Git 可视化 pull");
                var remoteBranch = repository.Branches[$"{remoteName}/{remoteBranchName}"]
                                   ?? throw new InvalidOperationException(
                                       $"获取后仍找不到远程分支 {remoteName}/{remoteBranchName}。");
                var mergeOptions = new MergeOptions
                {
                    FastForwardStrategy = strategy == PullStrategy.FastForwardOnly
                        ? FastForwardStrategy.FastForwardOnly
                        : FastForwardStrategy.Default
                };
                if (strategy == PullStrategy.Rebase)
                {
                    var committer = new Identity(signature.Name, signature.Email);
                    var rebase = repository.Rebase.Start(
                        repository.Head, remoteBranch, remoteBranch, committer, new RebaseOptions());
                    return GitOperationResult.Ok("pull", $"拉取变基状态：{rebase.Status}", command);
                }

                var result = repository.Merge(remoteBranch, signature, mergeOptions);
                return GitOperationResult.Ok("pull",
                    result.Status == MergeStatus.Conflicts ? "拉取产生冲突" : "拉取完成",
                    command, [$"状态：{result.Status}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> PushAsync(
        string repositoryPath,
        string remoteName,
        bool forceWithLease,
        RemoteCredential? credential = null,
        IProgress<GitPushProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var command = forceWithLease
            ? $"git push --force-with-lease --follow-tags {GitServiceSupport.Quote(remoteName)}"
            : $"git push --follow-tags {GitServiceSupport.Quote(remoteName)}";
        return ExecuteWriteAsync(repositoryPath, "push", command,
            forceWithLease ? GitOperationRisk.Dangerous : GitOperationRisk.Safe,
            forceWithLease, null, repository =>
            {
                var branch = repository.Head;
                if (branch.Tip is null)
                {
                    throw new InvalidOperationException("当前分支没有可推送的提交。");
                }
                var remote = repository.Network.Remotes[remoteName]
                             ?? throw new ArgumentException("远程不存在。");
                progress?.Report(new GitPushProgress(
                    GitPushProgressStage.Connecting,
                    Message: $"正在连接 {remote.Name}"));
                var pushUrl = string.IsNullOrWhiteSpace(remote.PushUrl) ? remote.Url : remote.PushUrl;
                var options = GitServiceSupport.PushOptions(pushUrl, credential);
                var destinationRef = $"refs/heads/{branch.FriendlyName}";
                var localTipId = branch.Tip.Id.Sha;
                var pushStatusErrors = new List<string>();
                options.OnPushStatusError = error => pushStatusErrors.Add(
                    string.IsNullOrWhiteSpace(error.Reference)
                        ? error.Message
                        : $"{error.Reference}：{error.Message}");
                var reachableCommitIds = repository.Commits
                    .QueryBy(new CommitFilter { IncludeReachableFrom = branch.Tip })
                    .Select(commit => commit.Id)
                    .ToHashSet();
                var remoteReferenceNames = repository.Network
                    .ListReferences(remote, options.CredentialsProvider)
                    .Select(reference => reference.CanonicalName)
                    .ToHashSet(StringComparer.Ordinal);
                var annotatedTagsToPush = repository.Tags
                    .Where(tag => tag.Annotation is not null)
                    .Where(tag => tag.PeeledTarget is Commit commit && reachableCommitIds.Contains(commit.Id))
                    .Where(tag => !remoteReferenceNames.Contains(tag.CanonicalName))
                    .OrderBy(tag => tag.FriendlyName, StringComparer.Ordinal)
                    .ToArray();
                var pushRefSpecs = new List<string>
                {
                    $"{branch.CanonicalName}:{destinationRef}"
                };
                pushRefSpecs.AddRange(annotatedTagsToPush.Select(
                    tag => $"{tag.CanonicalName}:{tag.CanonicalName}"));
                ObjectId? expectedRemoteTip = null;
                var leaseMismatch = false;
                string? recoveryReference = null;
                if (forceWithLease)
                {
                    var trackingReferenceName = $"refs/remotes/{remote.Name}/{branch.FriendlyName}";
                    var trackingReference = repository.Refs[trackingReferenceName]?.ResolveToDirectReference();
                    expectedRemoteTip = trackingReference is null
                        ? null
                        : repository.Lookup<Commit>(trackingReference.TargetIdentifier)?.Id;
                    if (expectedRemoteTip is null)
                    {
                        throw new InvalidOperationException(
                            $"本地没有 {remote.Name}/{branch.FriendlyName} 的已知远程状态。请先手动获取并检查，再重试。");
                    }
                    recoveryReference = CreateSafetyReference(
                        repository,
                        "remote-recovery",
                        expectedRemoteTip,
                        "Git Visualizer force-with-lease recovery");
                }
                options.OnNegotiationCompletedBeforePush = updates =>
                {
                    var negotiatedUpdates = updates.ToArray();
                    var updateCount = negotiatedUpdates.Length;
                    progress?.Report(new GitPushProgress(
                        GitPushProgressStage.Negotiating,
                        updateCount,
                        updateCount,
                        Message: $"已协商 {updateCount} 个引用更新"));
                    if (forceWithLease)
                    {
                        var branchUpdate = negotiatedUpdates.FirstOrDefault(update =>
                            string.Equals(
                                update.DestinationRefName,
                                destinationRef,
                                StringComparison.Ordinal));
                        leaseMismatch = branchUpdate is null ||
                                        branchUpdate.SourceObjectId != expectedRemoteTip;
                        if (leaseMismatch)
                        {
                            return false;
                        }
                    }
                    return !cancellationToken.IsCancellationRequested;
                };
                options.OnPackBuilderProgress = (stage, current, total) =>
                {
                    progress?.Report(new GitPushProgress(
                        GitPushProgressStage.Packing,
                        current,
                        total,
                        Message: stage.ToString()));
                    return !cancellationToken.IsCancellationRequested;
                };
                options.OnPushTransferProgress = (current, total, bytes) =>
                {
                    progress?.Report(new GitPushProgress(
                        GitPushProgressStage.Transferring,
                        current,
                        total,
                        bytes));
                    return !cancellationToken.IsCancellationRequested;
                };
                if (forceWithLease)
                {
                    pushRefSpecs[0] = $"+{pushRefSpecs[0]}";
                    try
                    {
                        repository.Network.Push(
                            remote,
                            pushRefSpecs,
                            options);
                    }
                    catch when (leaseMismatch)
                    {
                        throw new InvalidOperationException(
                            "远程分支已不同于本地上次获取的状态；租约校验失败，未发送强制更新。请先手动获取并检查。");
                    }
                    if (leaseMismatch)
                    {
                        throw new InvalidOperationException(
                            "远程分支已变化；租约校验失败，未发送强制更新。");
                    }
                }
                else
                {
                    repository.Network.Push(
                        remote,
                        pushRefSpecs,
                        options);
                }
                var confirmedRemoteTip = repository.Network
                    .ListReferences(remote, options.CredentialsProvider)
                    .FirstOrDefault(reference => string.Equals(
                        reference.CanonicalName,
                        destinationRef,
                        StringComparison.Ordinal))
                    ?.TargetIdentifier;
                EnsurePushWasAccepted(
                    destinationRef,
                    localTipId,
                    confirmedRemoteTip,
                    pushStatusErrors);
                progress?.Report(new GitPushProgress(
                    GitPushProgressStage.UpdatingTracking,
                    Message: "正在更新本地上游分支配置"));
                repository.Branches.Update(
                    branch,
                    updater => updater.Remote = remote.Name,
                    updater => updater.UpstreamBranch = $"refs/heads/{branch.FriendlyName}");
                var updatedTrackingReferenceName = $"refs/remotes/{remote.Name}/{branch.FriendlyName}";
                var updatedTrackingReference = repository.Refs[updatedTrackingReferenceName];
                if (updatedTrackingReference is null)
                {
                    repository.Refs.Add(
                        updatedTrackingReferenceName,
                        branch.Tip.Id,
                        "Git Visualizer push tracking update");
                }
                else
                {
                    repository.Refs.UpdateTarget(
                        updatedTrackingReference,
                        branch.Tip.Id,
                        "Git Visualizer push tracking update");
                }
                return GitOperationResult.Ok(
                    "push",
                    annotatedTagsToPush.Length == 0
                        ? "推送完成"
                        : $"推送完成，并上传 {annotatedTagsToPush.Length} 个附注标签",
                    command,
                    (recoveryReference is null
                        ? Array.Empty<string>()
                        : [$"远程旧状态安全引用：{recoveryReference}"])
                    .Concat(annotatedTagsToPush.Length == 0
                        ? Array.Empty<string>()
                        : [$"已上传附注标签：{string.Join("、", annotatedTagsToPush.Select(tag => tag.FriendlyName))}"])
                    .ToArray());
            }, cancellationToken: cancellationToken);
    }

    internal static void EnsurePushWasAccepted(
        string destinationRef,
        string localTipId,
        string? remoteTipId,
        IReadOnlyList<string> pushStatusErrors)
    {
        if (pushStatusErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"远程拒绝推送：{string.Join("；", pushStatusErrors)}");
        }

        if (string.IsNullOrWhiteSpace(remoteTipId))
        {
            throw new InvalidOperationException(
                $"推送未生效：远程没有创建目标分支 {destinationRef}。请检查仓库写入权限或分支保护规则。");
        }

        if (!string.Equals(localTipId, remoteTipId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"推送未生效：远程 {destinationRef} 仍停留在 {ShortObjectId(remoteTipId)}，" +
                $"本地提交为 {ShortObjectId(localTipId)}。请检查仓库写入权限或分支保护规则。");
        }
    }

    private static string ShortObjectId(string objectId) =>
        objectId[..Math.Min(7, objectId.Length)];

    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ConflictFile>>(() =>
        {
            using var repository = new Repository(repositoryPath);
            return repository.Index.Conflicts.Select(conflict =>
            {
                var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? string.Empty;
                var fullPath = Path.Combine(repository.Info.WorkingDirectory, path);
                var isBinary = IsBinary(ReadBlobBytes(repository, conflict.Ancestor)) ||
                               IsBinary(ReadBlobBytes(repository, conflict.Ours)) ||
                               IsBinary(ReadBlobBytes(repository, conflict.Theirs)) ||
                               (File.Exists(fullPath) && IsBinary(fullPath));
                return new ConflictFile(
                    path,
                    ReadBlob(repository, conflict.Ancestor),
                    ReadBlob(repository, conflict.Ours),
                    ReadBlob(repository, conflict.Theirs),
                    File.Exists(fullPath) && !isBinary
                        ? File.ReadAllText(fullPath)
                        : string.Empty,
                    isBinary,
                    false);
            }).ToArray();
        }, cancellationToken);

    public Task<GitOperationResult> ResolveConflictAsync(
        string repositoryPath, string path, string resultText,
        CancellationToken cancellationToken = default)
    {
        var command = $"git add -- {GitServiceSupport.Quote(path)}";
        return ExecuteWriteAsync(repositoryPath, "conflict-resolve", command, GitOperationRisk.Caution, false,
            [path], repository =>
            {
                var fullPath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, path));
                var conflict = repository.Index.Conflicts.FirstOrDefault(item =>
                    string.Equals(
                        item.Ours?.Path ?? item.Theirs?.Path ?? item.Ancestor?.Path,
                        path,
                        StringComparison.Ordinal));
                if (conflict is null)
                {
                    throw new InvalidOperationException("该文件当前不在冲突索引中。");
                }
                if (IsBinary(ReadBlobBytes(repository, conflict.Ancestor)) ||
                    IsBinary(ReadBlobBytes(repository, conflict.Ours)) ||
                    IsBinary(ReadBlobBytes(repository, conflict.Theirs)) ||
                    (File.Exists(fullPath) && IsBinary(fullPath)))
                {
                    throw new InvalidOperationException(
                        "二进制冲突不能通过文本编辑器解决；本版本已阻止可能破坏文件的文本写入。");
                }
                File.WriteAllText(fullPath, resultText);
                Commands.Stage(repository, path);
                return GitOperationResult.Ok("conflict-resolve", $"已标记 {path} 为已解决", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> ResolveBinaryConflictAsync(
        string repositoryPath, string path, ConflictSide side,
        CancellationToken cancellationToken = default)
    {
        if (side is not (ConflictSide.Ours or ConflictSide.Theirs or ConflictSide.CurrentFile))
        {
            return Task.FromResult(GitOperationResult.Fail(
                "binary-conflict-resolve", "git add -- <path>",
                new ArgumentException("二进制冲突只能采用当前版本、对方版本或工作区文件。", nameof(side))));
        }
        var command = side switch
        {
            ConflictSide.Ours => $"git checkout --ours -- {GitServiceSupport.Quote(path)} && git add -- {GitServiceSupport.Quote(path)}",
            ConflictSide.Theirs => $"git checkout --theirs -- {GitServiceSupport.Quote(path)} && git add -- {GitServiceSupport.Quote(path)}",
            _ => $"git add -- {GitServiceSupport.Quote(path)}"
        };
        return ExecuteWriteAsync(
            repositoryPath, "binary-conflict-resolve", command,
            GitOperationRisk.Caution, true, [path], repository =>
            {
                var conflict = repository.Index.Conflicts.FirstOrDefault(item =>
                    string.Equals(
                        item.Ours?.Path ?? item.Theirs?.Path ?? item.Ancestor?.Path,
                        path,
                        StringComparison.Ordinal));
                if (conflict is null)
                {
                    throw new InvalidOperationException("该文件当前不在冲突索引中。");
                }
                var isBinary = IsBinary(ReadBlobBytes(repository, conflict.Ancestor)) ||
                               IsBinary(ReadBlobBytes(repository, conflict.Ours)) ||
                               IsBinary(ReadBlobBytes(repository, conflict.Theirs));
                if (!isBinary)
                {
                    throw new InvalidOperationException("该冲突不是二进制冲突，请使用文本解决器。");
                }

                var normalizedRoot = Path.GetFullPath(repository.Info.WorkingDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, path));
                if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("冲突文件路径越出仓库工作区。", nameof(path));
                }

                IndexEntry? selectedEntry = side switch
                {
                    ConflictSide.Ours => conflict.Ours,
                    ConflictSide.Theirs => conflict.Theirs,
                    _ => null
                };
                if (side == ConflictSide.CurrentFile)
                {
                    if (!File.Exists(fullPath))
                    {
                        throw new FileNotFoundException("工作区文件不存在，不能采用当前文件。", fullPath);
                    }
                }
                else if (selectedEntry is null)
                {
                    File.Delete(fullPath);
                }
                else
                {
                    var selectedBytes = ReadBlobBytes(repository, selectedEntry);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    var temporaryPath = fullPath + $".resolve-{Guid.NewGuid():N}.tmp";
                    try
                    {
                        File.WriteAllBytes(temporaryPath, selectedBytes);
                        File.Move(temporaryPath, fullPath, true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                }
                Commands.Stage(repository, path);
                if (repository.Index.Conflicts.Any(item =>
                        string.Equals(
                            item.Ours?.Path ?? item.Theirs?.Path ?? item.Ancestor?.Path,
                            path,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Git 未能从冲突索引中移除该文件。");
                }
                var sourceName = side switch
                {
                    ConflictSide.Ours => "当前版本（ours）",
                    ConflictSide.Theirs => "对方版本（theirs）",
                    _ => "当前工作区文件"
                };
                return GitOperationResult.Ok(
                    "binary-conflict-resolve",
                    $"已按原始字节采用{sourceName}并解决 {path}",
                    command,
                    [$"来源：{sourceName}", "未进行任何文本编码或转换"]);
            }, cancellationToken: cancellationToken);
    }

    public async Task<GitOperationResult> ContinueOperationAsync(
        string repositoryPath, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var repository = new Repository(repositoryPath))
        {
            if (repository.Info.CurrentOperation == CurrentOperation.Bisect)
            {
                return GitOperationResult.Fail(
                    "continue", "git bisect good|bad|skip",
                    new InvalidOperationException(
                        "当前仓库正在执行 Git bisect；M0 不提供继续控制，请在终端使用 git bisect good、bad、skip 或 reset。"));
            }
        }
        return await ExecuteWriteAsync(repositoryPath, "continue", "git <operation> --continue",
            GitOperationRisk.Caution, true, null, repository =>
            {
                if (repository.Index.Conflicts.Any())
                {
                    throw new InvalidOperationException("仍有未解决冲突。");
                }
                if (repository.Info.CurrentOperation == CurrentOperation.Bisect)
                {
                    throw new InvalidOperationException(
                        "当前仓库正在执行 Git bisect；M0 不提供继续控制，请在终端使用 git bisect good、bad、skip 或 reset。");
                }
                var signature = GitServiceSupport.ResolveSignature(repository, identity);
                return repository.Info.CurrentOperation switch
                {
                    CurrentOperation.Rebase => ContinueRebase(repository, signature),
                    CurrentOperation.Merge => ContinueMerge(repository, signature),
                    CurrentOperation.CherryPick => ContinueCherryPick(repository, signature),
                    CurrentOperation.Revert => ContinueRevert(repository, signature),
                    _ => throw new InvalidOperationException("当前没有可继续的 Git 操作。")
                };
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitOperationResult> AbortOperationAsync(
        string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var repository = new Repository(repositoryPath))
        {
            if (repository.Info.CurrentOperation == CurrentOperation.Bisect)
            {
                return GitOperationResult.Fail(
                    "abort", "git bisect reset",
                    new InvalidOperationException(
                        "当前仓库正在执行 Git bisect；M0 不提供中止控制，请在终端运行 git bisect reset。"));
            }
        }
        return await ExecuteWriteAsync(repositoryPath, "abort", "git <operation> --abort",
            GitOperationRisk.Caution, true, null, repository =>
            {
                switch (repository.Info.CurrentOperation)
                {
                    case CurrentOperation.Bisect:
                        throw new InvalidOperationException(
                            "当前仓库正在执行 Git bisect；M0 不提供中止控制，请在终端运行 git bisect reset。");
                    case CurrentOperation.Rebase:
                        repository.Rebase.Abort();
                        break;
                    case CurrentOperation.Merge:
                    case CurrentOperation.CherryPick:
                    case CurrentOperation.Revert:
                        var originalHead = repository.Refs["ORIG_HEAD"]?.ResolveToDirectReference().TargetIdentifier;
                        var commit = originalHead is null ? repository.Head.Tip : repository.Lookup<Commit>(originalHead);
                        if (commit is not null)
                        {
                            repository.Reset(LibGitResetMode.Hard, commit);
                        }
                        DeleteOperationMessages(repository);
                        break;
                    default:
                        throw new InvalidOperationException("当前没有可中止的 Git 操作。");
                }
                return GitOperationResult.Ok("abort", "操作已中止，工作区已恢复", "git <operation> --abort");
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public GitOperationPreview Preview(string operation, params string[] affectedItems)
    {
        var (risk, command, recovery, description) = operation switch
        {
            "reset-hard" => (GitOperationRisk.Dangerous, "git reset --hard <commit>", true,
                "移动当前分支并覆盖暂存区和工作区。"),
            "rebase" => (GitOperationRisk.Dangerous, "git rebase <upstream>", true,
                "重写当前分支上的本地提交。"),
            "amend" => (GitOperationRisk.Dangerous, "git commit --amend", true,
                "替换上一条提交。"),
            "force-push" => (GitOperationRisk.Dangerous, "git push --force-with-lease", true,
                "重写远程分支历史。"),
            "discard" => (GitOperationRisk.Dangerous, "git restore -- <paths>", true,
                "覆盖选中文件的未提交修改。"),
            _ => (GitOperationRisk.Caution, $"git {operation}", false, "执行所选 Git 操作。")
        };
        return new GitOperationPreview(
            operation, description, command, risk, affectedItems, recovery,
            recovery ? "执行前创建自动恢复点。" : "此操作不会创建恢复点。");
    }

    private async Task<GitOperationResult> ExecuteWriteAsync(
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RecoveryPoint? recoveryPoint = null;
        try
        {
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
                recoveryPoint = await recoveryService.CreateAsync(
                    repositoryPath, operation, affectedPaths, cancellationToken).ConfigureAwait(false);
            }

            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var repository = new Repository(repositoryPath);
                return action(repository);
            }, cancellationToken).ConfigureAwait(false);
            result = result with { RecoveryPointId = recoveryPoint?.Id };
            await LogAsync(repositoryPath, result, risk, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var result = GitOperationResult.Fail(operation, equivalentCommand, exception) with
            {
                RecoveryPointId = recoveryPoint?.Id
            };
            await LogAsync(repositoryPath, result, risk, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task LogAsync(
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

    private static void EnsureClean(Repository repository)
    {
        if (repository.RetrieveStatus().Any(GitServiceSupport.IsMeaningfulChange))
        {
            throw new InvalidOperationException(
                "工作区存在已暂存或未暂存的未提交修改，请先提交或处理这些修改。");
        }
    }

    private static (string RelativePath, string FullPath) NormalizeWorkTreePath(
        Repository repository,
        string path)
    {
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("文件路径必须是仓库内的相对路径。", nameof(path));
        }

        var relativePath = path.Replace('\\', '/').TrimStart('/');
        if (relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("不能丢弃 Git 元数据目录中的文件。", nameof(path));
        }

        var normalizedRoot = Path.GetFullPath(repository.Info.WorkingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("文件路径越出仓库工作区。", nameof(path));
        }

        return (relativePath, fullPath);
    }

    private static void PrepareFileForOverwrite(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            throw new IOException($"目标路径是目录，无法恢复为文件：{fullPath}");
        }
        if (File.Exists(fullPath))
        {
            File.SetAttributes(fullPath, FileAttributes.Normal);
        }
    }

    private static void DeleteWorkTreeFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }
        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    private static string PreserveStash(Repository repository, int index)
    {
        var stash = repository.Stashes[index]
                    ?? throw new ArgumentOutOfRangeException(nameof(index), "临时现场不存在。");
        return CreateSafetyReference(
            repository,
            "stash-backup",
            stash.WorkTree.Id,
            "Git Visualizer stash safety backup");
    }

    private static string NormalizeRemoteAddress(string url)
    {
        if (!GitRemoteAddress.TryNormalize(url, out var normalized))
        {
            throw new ArgumentException(
                "远程仓库地址无效；HTTP/HTTPS 地址不得内嵌用户名、密码或访问令牌。",
                nameof(url));
        }

        return normalized;
    }

    private static string NormalizeStashMessage(string message)
    {
        var normalized = message.Trim();
        var separator = normalized.IndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 && separator + 2 < normalized.Length
            ? normalized[(separator + 2)..]
            : normalized;
    }

    private static string CreateSafetyReference(
        Repository repository,
        string category,
        ObjectId target,
        string logMessage)
    {
        var referenceName =
            $"refs/gitvisualizer/{category}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        repository.Refs.Add(referenceName, target, logMessage);
        PruneSafetyReferences(repository, category);
        return referenceName;
    }

    private static void PruneSafetyReferences(Repository repository, string category)
    {
        const int maxReferences = 50;
        var maxAge = TimeSpan.FromDays(30);
        var prefix = $"refs/gitvisualizer/{category}/";
        var references = repository.Refs
            .Where(reference => reference.CanonicalName.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(reference => reference.CanonicalName, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < references.Length; index++)
        {
            var name = references[index].CanonicalName;
            var timestamp = name[prefix.Length..].Split('-', 2)[0];
            var expired = DateTimeOffset.TryParseExact(
                timestamp, "yyyyMMddHHmmssfff", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var createdAt) &&
                DateTimeOffset.UtcNow - createdAt > maxAge;
            if (index >= maxReferences || expired)
            {
                repository.Refs.Remove(name);
            }
        }
    }

    private static Branch ResolveMainline(Repository repository) =>
        repository.Branches["main"] ??
        repository.Branches["master"] ??
        repository.Branches.FirstOrDefault(branch =>
            branch.IsCurrentRepositoryHead && !branch.IsRemote) ??
        throw new InvalidOperationException("找不到可用于合并判断的本地主线分支。");

    private static bool IsMergedInto(
        Branch branch,
        Branch mainline,
        Repository repository)
    {
        if (branch.Tip is null)
        {
            return true;
        }
        if (mainline.Tip is null)
        {
            return false;
        }

        var mergeBase = repository.ObjectDatabase.FindMergeBase(mainline.Tip, branch.Tip);
        return mergeBase?.Id == branch.Tip.Id;
    }

    private static List<GitHistoryEvent> ReadGitHistoryEvents(
        Repository repository,
        CancellationToken cancellationToken)
    {
        var result = new List<GitHistoryEvent>();
        var roots = ReadHistoryRoots(repository);
        var commits = roots.Count == 0
            ? repository.Commits
            : repository.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = roots,
                SortBy = CommitSortStrategies.Topological |
                         CommitSortStrategies.Time
            });

        foreach (var commit in commits.Take(5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commit.Parents.Skip(1).Any())
            {
                var sourceBranch = ExtractMergedBranchName(commit.MessageShort);
                result.Add(new GitHistoryEvent(
                    $"merge:{commit.Id.Sha}",
                    GitHistoryEventKind.Merge,
                    commit.Id.Sha,
                    commit.Parents.Skip(1).FirstOrDefault()?.Id.Sha,
                    sourceBranch,
                    sourceBranch is null
                        ? $"该节点为 merge commit，包含 {commit.Parents.Count()} 个父提交"
                        : $"分支 {sourceBranch} 在此合并，提交包含 {commit.Parents.Count()} 个父提交",
                    commit.Committer.When));
            }

            var revertedCommit = RevertedCommitPattern
                .Match(commit.Message)
                .Groups["id"]
                .Value;
            if (commit.MessageShort.StartsWith(
                    "Revert",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(revertedCommit))
            {
                result.Add(new GitHistoryEvent(
                    $"revert:{commit.Id.Sha}",
                    GitHistoryEventKind.Revert,
                    commit.Id.Sha,
                    string.IsNullOrEmpty(revertedCommit)
                        ? null
                        : revertedCommit,
                    null,
                    string.IsNullOrEmpty(revertedCommit)
                        ? "该提交用于撤销之前的修改；原提交历史仍然保留"
                        : $"该提交用于撤销 {revertedCommit[..Math.Min(8, revertedCommit.Length)]}；原提交历史仍然保留",
                    commit.Committer.When));
            }
        }

        var headLogPath = Path.Combine(repository.Info.Path, "logs", "HEAD");
        ReadReflogEvents(headLogPath, null, isHeadLog: true, result);

        var branchLogsPath = Path.Combine(
            repository.Info.Path,
            "logs",
            "refs",
            "heads");
        if (Directory.Exists(branchLogsPath))
        {
            try
            {
                foreach (var logPath in Directory.EnumerateFiles(
                             branchLogsPath,
                             "*",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var branchName = Path.GetRelativePath(
                            branchLogsPath,
                            logPath)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    ReadReflogEvents(
                        logPath,
                        branchName,
                        isHeadLog: false,
                        result);
                }
            }
            catch (IOException)
            {
                // Commit-derived merge/revert events remain available.
            }
            catch (UnauthorizedAccessException)
            {
                // Reflog access is optional for visualization.
            }
        }

        return result;
    }

    private static void ReadReflogEvents(
        string logPath,
        string? branchName,
        bool isHeadLog,
        ICollection<GitHistoryEvent> destination)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        try
        {
            var lineIndex = 0;
            foreach (var line in File.ReadLines(logPath))
            {
                lineIndex++;
                var match = ReflogLinePattern.Match(line);
                if (!match.Success ||
                    !long.TryParse(
                        match.Groups["time"].Value,
                        out var unixTime))
                {
                    continue;
                }

                var oldId = match.Groups["old"].Value;
                var newId = match.Groups["new"].Value;
                var message = match.Groups["message"].Value;
                var occurredAt = DateTimeOffset.FromUnixTimeSeconds(unixTime);
                var eventId = $"{Path.GetFullPath(logPath)}:{lineIndex}:{newId}";

                if (!isHeadLog)
                {
                    if (message.StartsWith(
                            "branch:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        destination.Add(new GitHistoryEvent(
                            $"branch-created:{branchName}:{newId}",
                            GitHistoryEventKind.BranchCreated,
                            newId,
                            IsZeroObjectId(oldId) ? null : oldId,
                            branchName,
                            $"分支 {branchName} 从此提交创建",
                            occurredAt));
                    }
                    else if (message.StartsWith(
                                 "commit",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        destination.Add(new GitHistoryEvent(
                            $"commit-created:{branchName}:{newId}",
                            GitHistoryEventKind.CommitCreated,
                            newId,
                            IsZeroObjectId(oldId) ? null : oldId,
                            branchName,
                            $"该提交由 {branchName} 分支产生",
                            occurredAt));
                    }
                    continue;
                }

                if (message.StartsWith(
                        "checkout:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var destinationBranch = ExtractCheckoutDestination(message);
                    destination.Add(new GitHistoryEvent(
                        $"checkout:{eventId}",
                        GitHistoryEventKind.Checkout,
                        newId,
                        IsZeroObjectId(oldId) ? null : oldId,
                        destinationBranch,
                        destinationBranch is null
                            ? $"checkout 将 HEAD 从 {ShortId(oldId)} 移动到提交 {ShortId(newId)}（Detached HEAD）"
                            : $"checkout 将 HEAD 从 {ShortId(oldId)} 移动到分支 {destinationBranch}（{ShortId(newId)}）",
                        occurredAt));
                }
                else if (message.StartsWith(
                             "reset:",
                             StringComparison.OrdinalIgnoreCase))
                {
                    destination.Add(new GitHistoryEvent(
                        $"reset:{eventId}",
                        GitHistoryEventKind.Reset,
                        newId,
                        IsZeroObjectId(oldId) ? null : oldId,
                        null,
                        $"reset 将当前分支指针移动到 {ShortId(newId)}",
                        occurredAt));
                }
            }
        }
        catch (IOException)
        {
            // A reflog can rotate while the graph refreshes.
        }
        catch (UnauthorizedAccessException)
        {
            // Reflog event annotations are best-effort.
        }
    }

    private static string? ExtractMergedBranchName(string message)
    {
        const string prefix = "Merge branch '";
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = message.IndexOf('\'', prefix.Length);
        return end <= prefix.Length
            ? null
            : message[prefix.Length..end];
    }

    private static string? ExtractCheckoutDestination(string message)
    {
        const string marker = " to ";
        var markerIndex = message.LastIndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || markerIndex + marker.Length >= message.Length)
        {
            return null;
        }

        var destination = message[(markerIndex + marker.Length)..];
        return destination.Length >= 7 &&
               destination.All(Uri.IsHexDigit)
            ? null
            : destination;
    }

    private static bool IsZeroObjectId(string id) =>
        id.All(character => character == '0');

    private static string ShortId(string id) =>
        id[..Math.Min(8, id.Length)];

    private static IReadOnlyList<Commit> ReadHistoryRoots(Repository repository)
    {
        var result = new Dictionary<string, Commit>(StringComparer.Ordinal);

        void AddCommit(Commit? commit)
        {
            if (commit is not null)
            {
                result.TryAdd(commit.Id.Sha, commit);
            }
        }

        AddCommit(repository.Head.Tip);
        foreach (var branch in repository.Branches)
        {
            AddCommit(branch.Tip);
        }
        foreach (var tag in repository.Tags)
        {
            AddCommit(tag.Target.Peel<Commit>());
        }

        var logsPath = Path.Combine(repository.Info.Path, "logs");
        if (!Directory.Exists(logsPath))
        {
            return result.Values.ToArray();
        }

        try
        {
            foreach (var logPath in Directory.EnumerateFiles(
                         logsPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadLines(logPath))
                {
                    var firstSpace = line.IndexOf(' ');
                    var secondSpace = firstSpace < 0
                        ? -1
                        : line.IndexOf(' ', firstSpace + 1);
                    if (firstSpace <= 0 || secondSpace <= firstSpace + 1)
                    {
                        continue;
                    }

                    AddCommit(repository.Lookup<Commit>(line[..firstSpace]));
                    AddCommit(repository.Lookup<Commit>(
                        line[(firstSpace + 1)..secondSpace]));
                }
            }
        }
        catch (IOException)
        {
            // Refs remain a complete fallback if a reflog changes during refresh.
        }
        catch (UnauthorizedAccessException)
        {
            // Some repositories are readable even when their reflogs are not.
        }

        return result.Values.ToArray();
    }

    private static CommitNode MapCommit(Commit commit) =>
        new(
            commit.Id.Sha,
            commit.Id.Sha[..Math.Min(8, commit.Id.Sha.Length)],
            commit.MessageShort,
            commit.Author.Name,
            commit.Author.Email,
            commit.Author.When,
            commit.Parents.Select(parent => parent.Id.Sha).ToArray());

    private static RepositoryFeatures DetectFeatures(Repository repository)
    {
        var root = repository.Info.WorkingDirectory;
        var hasLfs = File.Exists(Path.Combine(root, ".lfsconfig")) ||
                     (File.Exists(Path.Combine(root, ".gitattributes")) &&
                      File.ReadAllText(Path.Combine(root, ".gitattributes"))
                          .Contains("filter=lfs", StringComparison.OrdinalIgnoreCase));
        var hasSubmodules = File.Exists(Path.Combine(root, ".gitmodules"));
        var hooksPath = Path.Combine(repository.Info.Path, "hooks");
        var hasHooks = Directory.Exists(hooksPath) &&
                       Directory.EnumerateFiles(hooksPath)
                           .Any(path => !path.EndsWith(".sample", StringComparison.OrdinalIgnoreCase));
        var notices = new List<string>();
        if (hasLfs) notices.Add("检测到 Git LFS；V1 只显示状态，不管理 LFS 对象。");
        if (hasSubmodules) notices.Add("检测到子模块；V1 不执行子模块更新。");
        if (hasHooks) notices.Add("检测到自定义 Hooks；内置 Git 引擎不会执行这些脚本。");
        return new RepositoryFeatures(hasLfs, hasSubmodules, hasHooks, notices);
    }

    private static RepositoryOperationState MapOperation(CurrentOperation operation) => operation switch
    {
        CurrentOperation.None => RepositoryOperationState.None,
        CurrentOperation.Merge => RepositoryOperationState.Merge,
        CurrentOperation.Rebase => RepositoryOperationState.Rebase,
        CurrentOperation.CherryPick => RepositoryOperationState.CherryPick,
        CurrentOperation.Revert => RepositoryOperationState.Revert,
        CurrentOperation.Bisect => RepositoryOperationState.Bisect,
        _ => RepositoryOperationState.Unknown
    };

    private static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[8192];
        var count = stream.Read(bytes);
        return IsBinary(bytes[..count]);
    }

    private static void ValidateIdentity(GitIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Name) ||
            string.IsNullOrWhiteSpace(identity.Email) ||
            identity.Name.IndexOfAny(['\r', '\n']) >= 0 ||
            identity.Email.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("Git 用户名和邮箱不能为空或包含换行符。");
        }
    }

    private static bool IsBinary(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0);

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            preambleLength = Encoding.UTF8.GetPreamble().Length;
            return new UTF8Encoding(true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            preambleLength = Encoding.Unicode.GetPreamble().Length;
            return Encoding.Unicode;
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return new UTF8Encoding(false);
    }

    private static void AddTreeEntries(
        Tree tree,
        string prefix,
        ICollection<CommitTreeEntry> result,
        CancellationToken cancellationToken)
    {
        foreach (var entry in tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = string.IsNullOrEmpty(prefix)
                ? entry.Name
                : $"{prefix}/{entry.Name}";
            if (entry.Target is Tree childTree)
            {
                result.Add(new CommitTreeEntry(path, true));
                AddTreeEntries(childTree, path, result, cancellationToken);
            }
            else if (entry.Target is Blob blob)
            {
                using var content = blob.GetContentStream();
                var sample = new byte[8192];
                var count = content.Read(sample, 0, sample.Length);
                result.Add(new CommitTreeEntry(path, false, blob.Size, IsBinary(sample.AsSpan(0, count))));
            }
            else
            {
                result.Add(new CommitTreeEntry(path, false, 0, true));
            }
        }
    }

    private static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

    private static byte[] ReadBlobBytes(Repository repository, IndexEntry? entry)
    {
        if (entry is null)
        {
            return [];
        }
        var blob = repository.Lookup<Blob>(entry.Id);
        if (blob is null)
        {
            return [];
        }
        using var input = blob.GetContentStream();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadBlob(Repository repository, IndexEntry? entry)
    {
        var bytes = ReadBlobBytes(repository, entry);
        return IsBinary(bytes) ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static GitOperationResult ContinueRebase(Repository repository, Signature signature)
    {
        var result = repository.Rebase.Continue(
            new Identity(signature.Name, signature.Email), new RebaseOptions());
        return GitOperationResult.Ok(
            "continue",
            result.Status == RebaseStatus.Conflicts ? "变基仍有冲突" : "变基已继续",
            "git rebase --continue",
            [$"状态：{result.Status}", $"已完成：{result.CompletedStepCount}/{result.TotalStepCount}"]);
    }

    private static GitOperationResult ContinueMerge(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Merge conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "合并已完成", "git merge --continue", [commit.Id.Sha]);
    }

    private static GitOperationResult ContinueCherryPick(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Cherry-pick conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "拣选已完成", "git cherry-pick --continue", [commit.Id.Sha]);
    }

    private static GitOperationResult ContinueRevert(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Revert conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "撤销已完成", "git revert --continue", [commit.Id.Sha]);
    }

    private static string? ReadOperationMessage(Repository repository)
    {
        foreach (var name in new[] { "MERGE_MSG", "COMMIT_EDITMSG" })
        {
            var path = Path.Combine(repository.Info.Path, name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }
        return null;
    }

    private static void DeleteOperationMessages(Repository repository)
    {
        foreach (var name in new[] { "MERGE_MSG", "COMMIT_EDITMSG", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            var path = Path.Combine(repository.Info.Path, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
