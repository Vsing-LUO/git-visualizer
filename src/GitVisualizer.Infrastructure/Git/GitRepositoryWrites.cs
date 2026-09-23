// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

using static GitVisualizer.Infrastructure.Git.GitRepositorySupport;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class GitRepositoryWrites(GitOperationCoordinator operations, ISafeFileWriter safeWriter)
{
    public Task<GitOperationResult> SetGlobalIdentityAsync(
        GitIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LocalPaths.Default.IsIsolated)
                return IsolatedGlobalIdentityFailure();
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
        cancellationToken.ThrowIfCancellationRequested();
        if (global && LocalPaths.Default.IsIsolated)
            return Task.FromResult(IsolatedGlobalIdentityFailure());
        ValidateIdentity(identity);
        var level = global ? ConfigurationLevel.Global : ConfigurationLevel.Local;
        return operations.ExecuteWriteAsync(
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
        operations.ExecuteWriteAsync(path, "init", "git init", GitOperationRisk.Safe, false, null, repository =>
        {
            if (identity is not null)
            {
                ValidateIdentity(identity);
                repository.Config.Set("user.name", identity.Name.Trim(), ConfigurationLevel.Local);
                repository.Config.Set("user.email", identity.Email.Trim(), ConfigurationLevel.Local);
            }
            return GitOperationResult.Ok("init", "仓库初始化完成", "git init", [path]);
        }, initializeIfNeeded: true, cancellationToken);

    public Task<GitOperationResult> StageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git add -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return operations.ExecuteWriteAsync(repositoryPath, "stage", command, GitOperationRisk.Safe, false, paths, repository =>
        {
            Commands.Stage(repository, paths);
            return GitOperationResult.Ok("stage", $"已暂存 {paths.Count} 个文件", command, paths);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> UnstageFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git restore --staged -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return operations.ExecuteWriteAsync(repositoryPath, "unstage", command, GitOperationRisk.Safe, false, paths, repository =>
        {
            Commands.Unstage(repository, paths);
            return GitOperationResult.Ok("unstage", $"已取消暂存 {paths.Count} 个文件", command, paths);
        }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> DiscardFilesAsync(
        string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var command = $"git restore -- {string.Join(' ', paths.Select(GitServiceSupport.Quote))}";
        return operations.ExecuteWriteAsync(repositoryPath, "discard", command, GitOperationRisk.Dangerous, true, paths, repository =>
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
        return operations.ExecuteWriteAsync(repositoryPath, amend ? "amend" : "commit", command,
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
        return operations.ExecuteWriteAsync(repositoryPath, "branch-create", command, GitOperationRisk.Safe, false, null,
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

    public Task<GitOperationResult> RenameBranchAsync(
        string repositoryPath, string oldName, string newName,
        CancellationToken cancellationToken = default)
    {
        oldName = oldName.Trim();
        newName = newName.Trim();
        var command = $"git branch -m {GitServiceSupport.Quote(oldName)} {GitServiceSupport.Quote(newName)}";
        return operations.ExecuteWriteAsync(repositoryPath, "branch-rename", command, GitOperationRisk.Caution, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "branch-delete", command,
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

    public Task<GitOperationResult> MergeAsync(
        string repositoryPath, string branchName, GitIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var command = $"git merge {GitServiceSupport.Quote(branchName)}";
        return operations.ExecuteWriteAsync(repositoryPath, "merge", command, GitOperationRisk.Caution, true, null, repository =>
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
        return operations.ExecuteWriteAsync(repositoryPath, "cherry-pick", command, GitOperationRisk.Caution, true, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "revert", command, GitOperationRisk.Caution, true, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "rebase", command, GitOperationRisk.Dangerous, true, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "reset", command, risk, true, null, repository =>
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
        return operations.ExecuteWriteAsync(repositoryPath, "tag-create", command, GitOperationRisk.Safe, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "tag-delete", command, GitOperationRisk.Caution, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, "stash-save", command, GitOperationRisk.Safe, false, null,
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
        return operations.ExecuteWriteAsync(repositoryPath, pop ? "stash-pop" : "stash-apply", command,
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
        return operations.ExecuteWriteAsync(repositoryPath, "stash-delete", command, GitOperationRisk.Dangerous, true, null,
            repository =>
            {
                var backupReference = PreserveStash(repository, index);
                repository.Stashes.Remove(index);
                return GitOperationResult.Ok(
                    "stash-delete", "临时现场已删除", command,
                    [$"安全引用：{backupReference}"]);
            }, cancellationToken: cancellationToken);
    }

    public Task<GitOperationResult> ResolveConflictAsync(
        string repositoryPath, string path, string resultText,
        CancellationToken cancellationToken = default, TextDocument? originalDocument = null)
    {
        var command = $"git add -- {GitServiceSupport.Quote(path)}";
        return operations.ExecuteWriteAsync(repositoryPath, "conflict-resolve", command, GitOperationRisk.Caution, true,
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
                if (new[] { conflict.Ancestor, conflict.Ours, conflict.Theirs }
                    .Where(entry => entry is not null).DistinctBy(entry => entry!.Id).Any(entry =>
                    ReadBlobDocument(repository, path, entry).IsReadOnly))
                    throw new InvalidOperationException("二进制、编码不明或混合换行冲突不能安全文本编辑，请使用外部程序处理。");
                if (originalDocument is null ||
                    !string.Equals(Path.GetFullPath(originalDocument.Path), fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请重新打开冲突文件以取得原始字节快照。");
                void CheckIndex()
                {
                    if (originalDocument.ConflictIndexDigest is null ||
                        ConflictIndexDigest(repository) != originalDocument.ConflictIndexDigest)
                        throw new IOException("冲突索引已变化，请刷新并核对结果后重试。");
                }
                safeWriter.SaveUnderLockAsync(repositoryPath, originalDocument, resultText,
                    cancellationToken, CheckIndex).GetAwaiter().GetResult();
                try
                {
                    ConflictStageCheckpoint?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckIndex();
                    Commands.Stage(repository, path);
                }
                catch (Exception exception)
                {
                    return GitOperationResult.Fail("conflict-resolve", command, exception, "WorktreeSavedStageFailed") with
                    {
                        Summary = "文件已保存、暂存失败",
                        WorktreeSaved = true,
                        ExecutionStage = "stage-failed",
                        Details = ["文件内容已安全保存，冲突暂存未完成。请核对后仅重试暂存，不要自动重复写入。", exception.Message]
                    };
                }
                return GitOperationResult.Ok("conflict-resolve", $"已标记 {path} 为已解决", command)
                    with { WorktreeSaved = true, ExecutionStage = "completed" };

            }, cancellationToken: cancellationToken);
    }

    internal Action? ConflictStageCheckpoint { get; set; }

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
        return operations.ExecuteWriteAsync(
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
                var isBinary = IsBinaryBlob(repository, conflict.Ancestor) ||
                               IsBinaryBlob(repository, conflict.Ours) ||
                               IsBinaryBlob(repository, conflict.Theirs);
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
                    var selectedSize = repository.ObjectDatabase.RetrieveObjectMetadata(selectedEntry.Id).Size;
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    var temporaryPath = fullPath + $".resolve-{Guid.NewGuid():N}.tmp";
                    try
                    {
                        {
                            using var output = File.Create(temporaryPath);
                            if (selectedSize > TextFileStorage.EditLimit)
                            {
                                // LibGit2Sharp's Blob stream first materializes the native blob. A
                                // selected large binary side is an explicit file operation, so stream
                                // it with Git instead of retaining the conflict contents in memory.
                                CopyLargeBlobAsync(repositoryPath, selectedEntry.Id.Sha, output, cancellationToken)
                                    .GetAwaiter().GetResult();
                            }
                            else
                            {
                                var selectedBlob = repository.Lookup<Blob>(selectedEntry.Id) ?? throw new IOException("冲突版本不存在。");
                                using var input = selectedBlob.GetContentStream();
                                input.CopyTo(output, 64 * 1024);
                            }
                            if (output.Length != selectedSize) throw new IOException("冲突版本写入长度不一致。");
                        }
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
        return await operations.ExecuteWriteAsync(repositoryPath, "continue", "git <operation> --continue",
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
        return await operations.ExecuteWriteAsync(repositoryPath, "abort", "git <operation> --abort",
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
}
