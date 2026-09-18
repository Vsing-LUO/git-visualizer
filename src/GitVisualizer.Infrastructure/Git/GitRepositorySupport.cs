using GitVisualizer.Infrastructure.FileSystem;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;
using LibGit2Sharp;
using LibGitResetMode = LibGit2Sharp.ResetMode;

namespace GitVisualizer.Infrastructure.Git;

internal static class GitRepositorySupport
{
    internal static readonly Regex ReflogLinePattern = new(
        """^(?<old>[0-9a-fA-F]+) (?<new>[0-9a-fA-F]+) .+ <[^>]*> (?<time>\d+) [+-]\d{4}\t(?<message>.*)$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static readonly Regex RevertedCommitPattern = new(
        """This reverts commit (?<id>[0-9a-fA-F]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    internal static GitOperationResult IsolatedGlobalIdentityFailure() =>
        GitOperationResult.Fail("identity-config", "git config --global user.name <name>",
            new InvalidOperationException("隔离模式禁止修改全局 Git 身份；请仅保存到当前测试仓库。"),
            "IsolatedProfile");

    internal static string EventFingerprint(Repository repository)
    {
        var parts = new List<string> { repository.Info.Path, repository.Head.Tip?.Id.Sha ?? "" };
        foreach (var reference in repository.Refs)
            parts.Add(reference.CanonicalName + ":" + reference.ResolveToDirectReference().TargetIdentifier);
        var logs = Path.Combine(repository.Info.Path, "logs");
        if (Directory.Exists(logs))
            foreach (var file in Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                parts.Add($"{file}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            }
        return string.Join("|", parts.Order(StringComparer.Ordinal));
    }

    internal static bool HasDetails(
        IReadOnlyList<string> details,
        int requiredCount) =>
        details.Count >= requiredCount &&
        details.Take(requiredCount).All(detail =>
            !string.IsNullOrWhiteSpace(detail));

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

    internal static string ShortObjectId(string objectId) =>
        objectId[..Math.Min(7, objectId.Length)];

    internal static string ConflictIndexDigest(Repository repository)
    {
        using var stream = File.OpenRead(Path.Combine(repository.Info.Path, "index"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    internal static void EnsureClean(Repository repository)
    {
        if (repository.RetrieveStatus().Any(GitServiceSupport.IsMeaningfulChange))
        {
            throw new InvalidOperationException(
                "工作区存在已暂存或未暂存的未提交修改，请先提交或处理这些修改。");
        }
    }

    internal static (string RelativePath, string FullPath) NormalizeWorkTreePath(
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

    internal static void PrepareFileForOverwrite(string fullPath)
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

    internal static void DeleteWorkTreeFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }
        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    internal static string PreserveStash(Repository repository, int index)
    {
        var stash = repository.Stashes[index]
                    ?? throw new ArgumentOutOfRangeException(nameof(index), "临时现场不存在。");
        return CreateSafetyReference(
            repository,
            "stash-backup",
            stash.WorkTree.Id,
            "Git Visualizer stash safety backup");
    }

    internal static string NormalizeRemoteAddress(string url)
    {
        if (!GitRemoteAddress.TryNormalize(url, out var normalized))
        {
            throw new ArgumentException(
                "远程仓库地址无效；HTTP/HTTPS 地址不得内嵌用户名、密码或访问令牌。",
                nameof(url));
        }

        return normalized;
    }

    internal static string NormalizeStashMessage(string message)
    {
        var normalized = message.Trim();
        var separator = normalized.IndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 && separator + 2 < normalized.Length
            ? normalized[(separator + 2)..]
            : normalized;
    }

    internal static string CreateSafetyReference(
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

    internal static void PruneSafetyReferences(Repository repository, string category)
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

    internal static Branch ResolveMainline(Repository repository) =>
        repository.Branches["main"] ??
        repository.Branches["master"] ??
        repository.Branches.FirstOrDefault(branch =>
            branch.IsCurrentRepositoryHead && !branch.IsRemote) ??
        throw new InvalidOperationException("找不到可用于合并判断的本地主线分支。");

    internal static bool IsMergedInto(
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

    internal static List<GitHistoryEvent> ReadGitHistoryEvents(
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

    internal static void ReadReflogEvents(
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

    internal static string? ExtractMergedBranchName(string message)
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

    internal static string? ExtractCheckoutDestination(string message)
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

    internal static bool IsZeroObjectId(string id) =>
        id.All(character => character == '0');

    internal static string ShortId(string id) =>
        id[..Math.Min(8, id.Length)];

    internal static IReadOnlyList<Commit> ReadHistoryRoots(Repository repository)
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

    internal static CommitNode MapCommit(Commit commit) =>
        new(
            commit.Id.Sha,
            commit.Id.Sha[..Math.Min(8, commit.Id.Sha.Length)],
            commit.MessageShort,
            commit.Author.Name,
            commit.Author.Email,
            commit.Author.When,
            commit.Parents.Select(parent => parent.Id.Sha).ToArray());

    internal static RepositoryFeatures DetectFeatures(Repository repository)
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

    internal static RepositoryOperationState MapOperation(CurrentOperation operation) => operation switch
    {
        CurrentOperation.None => RepositoryOperationState.None,
        CurrentOperation.Merge => RepositoryOperationState.Merge,
        CurrentOperation.Rebase => RepositoryOperationState.Rebase,
        CurrentOperation.CherryPick => RepositoryOperationState.CherryPick,
        CurrentOperation.Revert => RepositoryOperationState.Revert,
        CurrentOperation.Bisect => RepositoryOperationState.Bisect,
        _ => RepositoryOperationState.Unknown
    };

    internal static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[8192];
        var count = stream.Read(bytes);
        return IsBinary(bytes[..count]);
    }

    internal static void ValidateIdentity(GitIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Name) ||
            string.IsNullOrWhiteSpace(identity.Email) ||
            identity.Name.IndexOfAny(['\r', '\n']) >= 0 ||
            identity.Email.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("Git 用户名和邮箱不能为空或包含换行符。");
        }
    }

    internal static bool IsBinary(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0);

    internal static void AddTreeEntries(
        Repository repository, Tree tree,
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
                AddTreeEntries(repository, childTree, path, result, cancellationToken);
            }
            else if (entry.Target is Blob blob)
            {
                var size = repository.ObjectDatabase.RetrieveObjectMetadata(blob.Id).Size;
                var binary = false;
                if (size <= TextFileStorage.EditLimit)
                {
                    using var content = blob.GetContentStream();
                    var sample = new byte[(int)Math.Min(size, 8192)];
                    content.ReadExactly(sample);
                    binary = IsBinary(sample);
                }
                result.Add(new CommitTreeEntry(path, false, size, binary));
            }
            else
            {
                result.Add(new CommitTreeEntry(path, false, 0, true));
            }
        }
    }

    internal static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

    internal static TextDocument ReadBlobDocument(Repository repository, string path, IndexEntry? entry)
    {
        if (entry is null) return TextFileStorage.Decode(path, []);
        var size = repository.ObjectDatabase.RetrieveObjectMetadata(entry.Id).Size;
        if (size > TextFileStorage.EditLimit) return TextFileStorage.Oversized(path, size);
        var blob = repository.Lookup<Blob>(entry.Id) ?? throw new IOException("冲突版本不存在。");
        using var input = blob.GetContentStream();
        var bytes = new byte[checked((int)size)];
        input.ReadExactly(bytes);
        return TextFileStorage.Decode(path, bytes);
    }

    internal static bool IsBinaryBlob(Repository repository, IndexEntry? entry)
    {
        if (entry is null) return false;
        var size = repository.ObjectDatabase.RetrieveObjectMetadata(entry.Id).Size;
        // An oversized side is always external-only and can only be resolved by taking
        // whole bytes. Treat it as binary without loading native Blob contents.
        if (size > TextFileStorage.EditLimit) return true;
        var blob = repository.Lookup<Blob>(entry.Id) ?? throw new IOException("冲突版本不存在。");
        using var input = blob.GetContentStream();
        var bytes = new byte[(int)Math.Min(size, TextFileStorage.SampleLimit)];
        input.ReadExactly(bytes);
        return IsBinary(bytes);
    }

    internal static async Task CopyLargeBlobAsync(string repositoryPath, string objectId, Stream output, CancellationToken token)
    {
        // LibGit2Sharp.GetContentStream materializes native Blob memory. Use Git's streaming
        // cat-file path for an explicitly requested large export, with no filters or hooks.
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory.Trim('"'), "git.exe")));
        var executable = candidates.FirstOrDefault(File.Exists)
            ?? throw new IOException("导出超过 5 MiB 的历史文件需要系统 Git；请安装 Git 后重试。");
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            start.Environment.Remove(name);
        foreach (var argument in new[] { "-C", repositoryPath, "cat-file", "blob", objectId }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("无法启动 Git 导出历史文件。");
        try
        {
            var error = process.StandardError.ReadToEndAsync(token);
            await process.StandardOutput.BaseStream.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Git 导出失败：" + message);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
        }
    }

    internal static GitOperationResult ContinueRebase(Repository repository, Signature signature)
    {
        var result = repository.Rebase.Continue(
            new Identity(signature.Name, signature.Email), new RebaseOptions());
        return GitOperationResult.Ok(
            "continue",
            result.Status == RebaseStatus.Conflicts ? "变基仍有冲突" : "变基已继续",
            "git rebase --continue",
            [$"状态：{result.Status}", $"已完成：{result.CompletedStepCount}/{result.TotalStepCount}"]);
    }

    internal static GitOperationResult ContinueMerge(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Merge conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "合并已完成", "git merge --continue", [commit.Id.Sha]);
    }

    internal static GitOperationResult ContinueCherryPick(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Cherry-pick conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "拣选已完成", "git cherry-pick --continue", [commit.Id.Sha]);
    }

    internal static GitOperationResult ContinueRevert(Repository repository, Signature signature)
    {
        var message = ReadOperationMessage(repository);
        var commit = repository.Commit(
            string.IsNullOrWhiteSpace(message) ? "Revert conflict resolution" : message,
            signature, signature);
        return GitOperationResult.Ok("continue", "撤销已完成", "git revert --continue", [commit.Id.Sha]);
    }

    internal static string? ReadOperationMessage(Repository repository)
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

    internal static void DeleteOperationMessages(Repository repository)
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
