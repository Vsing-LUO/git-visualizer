using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

using static GitVisualizer.Infrastructure.Git.GitRepositorySupport;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class GitRemoteOperations(GitOperationCoordinator operations)
{
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
            return await Diagnostics.OperationResultLogging.PersistAsync(result,
                value => operations.LogAsync(path, value, GitOperationRisk.Safe, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var result = GitOperationResult.Fail("clone", command, exception);
            return await Diagnostics.OperationResultLogging.PersistAsync(result,
                value => operations.LogAsync(path, value, GitOperationRisk.Safe, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    public Task<GitOperationResult> AddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken = default)
    {
        var command = $"git remote add {GitServiceSupport.Quote(name)} <remote-url>";
        return operations.ExecuteWriteAsync(repositoryPath, "remote-add", command, GitOperationRisk.Safe, false, null,
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
        return operations.ExecuteWriteAsync(
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
        return operations.ExecuteWriteAsync(repositoryPath, "remote-remove", command, GitOperationRisk.Caution, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "fetch", command, GitOperationRisk.Safe, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "pull", command, GitOperationRisk.Caution, true, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "push", command,
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
}
