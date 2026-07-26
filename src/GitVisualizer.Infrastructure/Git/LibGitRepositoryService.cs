using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitRepositoryService : IGitRepositoryService
{
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

    public Task<GitOperationResult> SetIdentityAsync(
        string repositoryPath,
        GitIdentity identity,
        bool global,
        CancellationToken cancellationToken = default)
    {
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
                repository.Config.Set("user.name", identity.Name, level);
                repository.Config.Set("user.email", identity.Email, level);
                return GitOperationResult.Ok(
                    "identity-config",
                    global ? "默认 Git 身份已更新" : "仓库 Git 身份已更新",
                    $"git config {(global ? "--global " : string.Empty)}user.name <name>",
                    [$"{identity.Name} <{identity.Email}>"]);
            },
            cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> InitializeAsync(
        string path, GitIdentity identity, CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(path, "init", "git init", GitOperationRisk.Safe, false, null, repository =>
        {
            repository.Config.Set("user.name", identity.Name, ConfigurationLevel.Local);
            repository.Config.Set("user.email", identity.Email, ConfigurationLevel.Local);
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
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new IOException("克隆目标文件夹必须为空。");
            }

            await Task.Run(
                () => Repository.Clone(url, path, GitServiceSupport.CloneOptions(credential)),
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
            var changes = status.Select(entry =>
            {
                var fullPath = Path.Combine(repository.Info.WorkingDirectory, entry.FilePath);
                var info = new FileInfo(fullPath);
                return new FileChange(
                    entry.FilePath,
                    null,
                    GitServiceSupport.MapStatus(entry.State),
                    GitServiceSupport.IsStaged(entry.State),
                    info.Exists ? info.Length : 0,
                    info.Exists && IsBinary(fullPath));
            }).ToArray();

            var branches = repository.Branches.Select(branch =>
            {
                var divergence = branch.TrackedBranch is null
                    ? null
                    : repository.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, branch.TrackedBranch.Tip);
                return new BranchInfo(
                    branch.FriendlyName,
                    branch.CanonicalName,
                    branch.Tip?.Id.Sha ?? string.Empty,
                    branch.IsCurrentRepositoryHead,
                    branch.IsRemote,
                    branch.TrackedBranch?.FriendlyName,
                    divergence?.AheadBy ?? 0,
                    divergence?.BehindBy ?? 0);
            }).OrderByDescending(x => x.IsCurrent).ThenBy(x => x.IsRemote).ThenBy(x => x.FriendlyName).ToArray();

            var tags = repository.Tags.Select(tag =>
                    new TagInfo(tag.FriendlyName, tag.Target.Peel<GitObject>().Id.Sha))
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
            return new RepositorySnapshot(
                Path.GetFullPath(repositoryPath),
                repository.Info.WorkingDirectory,
                repository.Head.Tip?.Id.Sha ?? string.Empty,
                repository.Head.FriendlyName,
                repository.Info.IsBare,
                repository.Info.IsHeadDetached,
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
            var decorations = BuildDecorations(repository);
            return repository.Commits
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 1000))
                .Select(commit => new CommitNode(
                    commit.Id.Sha,
                    commit.Id.Sha[..Math.Min(8, commit.Id.Sha.Length)],
                    commit.MessageShort,
                    commit.Author.Name,
                    commit.Author.Email,
                    commit.Author.When,
                    commit.Parents.Select(parent => parent.Id.Sha).ToArray(),
                    decorations.TryGetValue(commit.Id.Sha, out var values) ? values : []))
                .ToArray();
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
            var tracked = paths.Where(path => repository.RetrieveStatus(path) != FileStatus.NewInWorkdir).ToArray();
            if (tracked.Length > 0)
            {
                repository.CheckoutPaths(repository.Head.FriendlyName, tracked, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force
                });
            }

            foreach (var path in paths.Except(tracked, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, path));
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            return GitOperationResult.Ok("discard", $"已放弃 {paths.Count} 个文件的修改", command, paths);
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
                    [commit.Id.Sha, commit.MessageShort]);
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
                return GitOperationResult.Ok("branch-create", $"已创建分支 {branch.FriendlyName}", command);
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
                Commands.Checkout(repository, branch);
                return GitOperationResult.Ok("branch-checkout", $"已切换到 {branch.FriendlyName}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> RenameBranchAsync(
        string repositoryPath, string oldName, string newName,
        CancellationToken cancellationToken = default)
    {
        var command = $"git branch -m {GitServiceSupport.Quote(oldName)} {GitServiceSupport.Quote(newName)}";
        return ExecuteWriteAsync(repositoryPath, "branch-rename", command, GitOperationRisk.Caution, false, null,
            repository =>
            {
                var branch = repository.Branches[oldName] ?? throw new ArgumentException("分支不存在。");
                repository.Branches.Rename(branch, newName);
                return GitOperationResult.Ok("branch-rename", $"分支已重命名为 {newName}", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DeleteBranchAsync(
        string repositoryPath, string name, bool force, CancellationToken cancellationToken = default)
    {
        var command = $"git branch {(force ? "-D" : "-d")} {GitServiceSupport.Quote(name)}";
        return ExecuteWriteAsync(repositoryPath, "branch-delete", command,
            force ? GitOperationRisk.Dangerous : GitOperationRisk.Caution, force, null, repository =>
            {
                var branch = repository.Branches[name] ?? throw new ArgumentException("分支不存在。");
                if (branch.IsCurrentRepositoryHead)
                {
                    throw new InvalidOperationException("不能删除当前分支。");
                }
                if (!force && repository.Head.Tip is not null && branch.Tip is not null)
                {
                    var mergeBase = repository.ObjectDatabase.FindMergeBase(repository.Head.Tip, branch.Tip);
                    if (mergeBase?.Id != branch.Tip.Id)
                    {
                        throw new InvalidOperationException("分支尚未完全合并，请使用强制删除并确认风险。");
                    }
                }
                repository.Branches.Remove(branch);
                return GitOperationResult.Ok("branch-delete", $"已删除分支 {name}", command);
            }, cancellationToken: cancellationToken);
    }

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
                [$"状态：{result.Status}"],
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
                    command, [$"状态：{result.Status}"]);
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
            var nativeMode = mode switch
            {
                GitResetMode.Soft => LibGitResetMode.Soft,
                GitResetMode.Mixed => LibGitResetMode.Mixed,
                GitResetMode.Hard => LibGitResetMode.Hard,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            repository.Reset(nativeMode, commit);
            return GitOperationResult.Ok("reset", $"已完成 {option} reset", command);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> CreateTagAsync(
        string repositoryPath, string name, string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git tag {GitServiceSupport.Quote(name)} {targetId ?? string.Empty}".TrimEnd();
        return ExecuteWriteAsync(repositoryPath, "tag-create", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                var target = targetId is null ? repository.Head.Tip : repository.Lookup<GitObject>(targetId);
                if (target is null)
                {
                    throw new ArgumentException("标签目标不存在。");
                }
                repository.ApplyTag(name, target.Sha);
                return GitOperationResult.Ok("tag-create", $"已创建标签 {name}", command);
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

    public Task<GitOperationResult> ApplyStashAsync(
        string repositoryPath, int index, bool pop, CancellationToken cancellationToken = default)
    {
        var command = $"git stash {(pop ? "pop" : "apply")} stash@{{{index}}}";
        return ExecuteWriteAsync(repositoryPath, pop ? "stash-pop" : "stash-apply", command,
            GitOperationRisk.Caution, true, null, repository =>
            {
                var status = pop ? repository.Stashes.Pop(index) : repository.Stashes.Apply(index);
                return GitOperationResult.Ok(
                    pop ? "stash-pop" : "stash-apply",
                    status == StashApplyStatus.Conflicts ? "恢复现场时产生冲突" : "工作现场已恢复",
                    command, [$"状态：{status}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DeleteStashAsync(
        string repositoryPath, int index, CancellationToken cancellationToken = default)
    {
        var command = $"git stash drop stash@{{{index}}}";
        return ExecuteWriteAsync(repositoryPath, "stash-delete", command, GitOperationRisk.Dangerous, true, null,
            repository =>
            {
                repository.Stashes.Remove(index);
                return GitOperationResult.Ok("stash-delete", "临时现场已删除", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> AddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken = default)
    {
        var command = $"git remote add {GitServiceSupport.Quote(name)} <remote-url>";
        return ExecuteWriteAsync(repositoryPath, "remote-add", command, GitOperationRisk.Safe, false, null,
            repository =>
            {
                repository.Network.Remotes.Add(name, url);
                return GitOperationResult.Ok("remote-add", $"已添加远程 {name}", command);
            }, cancellationToken: cancellationToken);
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
                var options = GitServiceSupport.FetchOptions(credential);
                options.Prune = true;
                Commands.Fetch(repository, remote.Name,
                    remote.FetchRefSpecs.Select(x => x.Specification), options, "Git 可视化 fetch");
                return GitOperationResult.Ok("fetch", $"已获取 {remoteName} 的更新", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> PullAsync(
        string repositoryPath,
        PullStrategy strategy,
        RemoteCredential? credential = null,
        GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = strategy switch
        {
            PullStrategy.Rebase => "git pull --rebase",
            PullStrategy.FastForwardOnly => "git pull --ff-only",
            _ => "git pull --no-rebase"
        };
        return ExecuteWriteAsync(repositoryPath, "pull", command, GitOperationRisk.Caution, true, null,
            repository =>
            {
                EnsureClean(repository);
                var signature = GitServiceSupport.ResolveSignature(repository, identity);
                var options = new PullOptions
                {
                    FetchOptions = GitServiceSupport.FetchOptions(credential),
                    MergeOptions = new MergeOptions
                    {
                        FastForwardStrategy = strategy == PullStrategy.FastForwardOnly
                            ? FastForwardStrategy.FastForwardOnly
                            : FastForwardStrategy.Default
                    }
                };
                if (strategy == PullStrategy.Rebase)
                {
                    var tracked = repository.Head.TrackedBranch
                                  ?? throw new InvalidOperationException("当前分支没有上游分支。");
                    Commands.Fetch(repository, tracked.RemoteName,
                        repository.Network.Remotes[tracked.RemoteName].FetchRefSpecs.Select(x => x.Specification),
                        options.FetchOptions, "Git 可视化 pull --rebase");
                    var committer = new Identity(signature.Name, signature.Email);
                    var rebase = repository.Rebase.Start(repository.Head, tracked, tracked, committer, new RebaseOptions());
                    return GitOperationResult.Ok("pull", $"拉取变基状态：{rebase.Status}", command);
                }

                var result = Commands.Pull(repository, signature, options);
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
        CancellationToken cancellationToken = default)
    {
        var command = forceWithLease ? "git push --force-with-lease" : $"git push {remoteName}";
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
                var options = GitServiceSupport.PushOptions(credential);
                if (forceWithLease)
                {
                    var tracked = branch.TrackedBranch;
                    if (tracked is null)
                    {
                        throw new InvalidOperationException("强制推送要求当前分支已有上游分支。");
                    }
                    var expectedRemoteTip = tracked.Tip?.Id;
                    Commands.Fetch(repository, remote.Name,
                        remote.FetchRefSpecs.Select(x => x.Specification),
                        GitServiceSupport.FetchOptions(credential), "force-with-lease preflight");
                    if (expectedRemoteTip != branch.TrackedBranch?.Tip?.Id)
                    {
                        throw new InvalidOperationException("远程分支在获取后发生变化，已取消强制推送。");
                    }
                    repository.Network.Push(remote,
                        $"+{branch.CanonicalName}:refs/heads/{branch.FriendlyName}", options);
                }
                else
                {
                    repository.Network.Push(
                        remote,
                        $"{branch.CanonicalName}:refs/heads/{branch.FriendlyName}",
                        options);
                }
                repository.Branches.Update(
                    branch,
                    updater => updater.Remote = remote.Name,
                    updater => updater.UpstreamBranch = $"refs/heads/{branch.FriendlyName}");
                return GitOperationResult.Ok("push", "推送完成", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(
        string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ConflictFile>>(() =>
        {
            using var repository = new Repository(repositoryPath);
            return repository.Index.Conflicts.Select(conflict =>
            {
                var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? string.Empty;
                return new ConflictFile(
                    path,
                    ReadBlob(repository, conflict.Ancestor),
                    ReadBlob(repository, conflict.Ours),
                    ReadBlob(repository, conflict.Theirs),
                    File.Exists(Path.Combine(repository.Info.WorkingDirectory, path))
                        ? File.ReadAllText(Path.Combine(repository.Info.WorkingDirectory, path))
                        : string.Empty,
                    IsBinary(ReadBlobBytes(repository, conflict.Ours)) ||
                    IsBinary(ReadBlobBytes(repository, conflict.Theirs)),
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
                File.WriteAllText(fullPath, resultText);
                Commands.Stage(repository, path);
                return GitOperationResult.Ok("conflict-resolve", $"已标记 {path} 为已解决", command);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> ContinueOperationAsync(
        string repositoryPath, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(repositoryPath, "continue", "git <operation> --continue",
            GitOperationRisk.Caution, true, null, repository =>
            {
                if (repository.Index.Conflicts.Any())
                {
                    throw new InvalidOperationException("仍有未解决冲突。");
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
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> AbortOperationAsync(
        string repositoryPath, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(repositoryPath, "abort", "git <operation> --abort",
            GitOperationRisk.Caution, true, null, repository =>
            {
                switch (repository.Info.CurrentOperation)
                {
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
            }, cancellationToken: cancellationToken);
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
            result.ErrorCode), cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureClean(Repository repository)
    {
        if (repository.RetrieveStatus().IsDirty)
        {
            throw new InvalidOperationException("工作区存在未提交修改，请先提交、暂存工作现场或取消操作。");
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildDecorations(Repository repository)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var branch in repository.Branches)
        {
            if (branch.Tip is null)
            {
                continue;
            }
            if (!result.TryGetValue(branch.Tip.Id.Sha, out var values))
            {
                values = [];
                result.Add(branch.Tip.Id.Sha, values);
            }
            values.Add(branch.FriendlyName);
        }
        foreach (var tag in repository.Tags)
        {
            var id = tag.Target.Peel<GitObject>().Id.Sha;
            if (!result.TryGetValue(id, out var values))
            {
                values = [];
                result.Add(id, values);
            }
            values.Add($"tag:{tag.FriendlyName}");
        }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
    }

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
        return bytes[..count].Contains((byte)0);
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
