using System.Text;
using GitVisualizer.Core;
using LibGit2Sharp;

namespace GitVisualizer.Infrastructure.Git;

public sealed class LibGitIndexPatchService(IOperationLogStore operationLog) : IIndexPatchService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<GitOperationResult> StageHunksAsync(
        string repositoryPath, string path, IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(repositoryPath, path, hunks, reverse: false, cancellationToken);

    public Task<GitOperationResult> UnstageHunksAsync(
        string repositoryPath, string path, IReadOnlyList<DiffHunk> hunks,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(repositoryPath, path, hunks, reverse: true, cancellationToken);

    private async Task<GitOperationResult> ApplyAsync(
        string repositoryPath, string path, IReadOnlyList<DiffHunk> hunks,
        bool reverse, CancellationToken cancellationToken)
    {
        var operation = reverse ? "hunk-unstage" : "hunk-stage";
        var command = reverse
            ? $"git apply --cached --reverse <selected-hunks:{path}>"
            : $"git apply --cached <selected-hunks:{path}>";
        var gate = GitServiceSupport.LockFor(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (hunks.Count == 0 ||
                hunks.Any(hunk => !hunk.Path.Equals(path, StringComparison.Ordinal) || hunk.IsStaged != reverse))
            {
                throw new ArgumentException("请选择同一文件、同一区域中的至少一个差异块。");
            }
            using (var repository = new Repository(repositoryPath))
            {
                var currentSnapshot = LibGitDiffService.ComputeSnapshot(repository, path);
                if (hunks.Any(hunk => !hunk.SnapshotId.Equals(currentSnapshot, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("文件已在差异生成后发生变化，请刷新后重新选择。");
                }
                ApplyToIndex(repository, path, hunks, reverse);
            }
            var result = GitOperationResult.Ok(
                operation,
                reverse ? $"已取消暂存 {hunks.Count} 个差异块" : $"已暂存 {hunks.Count} 个差异块",
                command,
                hunks.Select(hunk => hunk.Header));
            await LogAsync(repositoryPath, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var result = GitOperationResult.Fail(operation, command, exception);
            await LogAsync(repositoryPath, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task LogAsync(string repositoryPath, GitOperationResult result, CancellationToken cancellationToken) =>
        operationLog.AddAsync(new OperationLogEntry(
            Guid.NewGuid().ToString("N"), DateTimeOffset.Now, repositoryPath,
            result.Operation, result.Success, GitOperationRisk.Safe, result.Summary,
            result.EquivalentCommand, null, result.ErrorCode, result.Details), cancellationToken);

    private static void ApplyToIndex(
        Repository repository, string path, IReadOnlyList<DiffHunk> hunks, bool reverse)
    {
        ValidateRelativePath(path);
        var indexEntry = repository.Index[path];
        var source = indexEntry is null ? [] : ReadBlob(repository, indexEntry.Id);
        var text = DecodeText(source, out var hasBom, out var hasFinalNewLine);
        var lines = SplitLines(text, hasFinalNewLine);

        foreach (var hunk in hunks
                     .DistinctBy(item => item.Id)
                     .OrderByDescending(item => reverse ? item.NewStart : item.OldStart))
        {
            ApplyHunk(lines, hunk, reverse);
        }

        var removesPath = hunks.Any(hunk => reverse
            ? hunk.Patch.Contains("--- /dev/null", StringComparison.Ordinal)
            : hunk.Patch.Contains("+++ /dev/null", StringComparison.Ordinal));
        if (removesPath)
        {
            repository.Index.Remove(path);
            repository.Index.Write();
            return;
        }

        Mode? baselineMode = null;
        if (source.Length == 0 && !reverse)
        {
            var workTreePath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, path));
            var workingBytes = File.Exists(workTreePath) ? File.ReadAllBytes(workTreePath) : [];
            hasBom = workingBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            hasFinalNewLine = EndsWithNewLine(workingBytes);
        }
        else if (source.Length == 0 && reverse)
        {
            var headEntry = repository.Head.Tip?.Tree[path.Replace('\\', '/')];
            if (headEntry?.Target is Blob headBlob)
            {
                using var headStream = headBlob.GetContentStream();
                using var headBytes = new MemoryStream();
                headStream.CopyTo(headBytes);
                var bytesAtHead = headBytes.ToArray();
                hasBom = bytesAtHead.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                hasFinalNewLine = EndsWithNewLine(bytesAtHead);
                baselineMode = headEntry.Mode;
            }
        }

        var normalized = string.Join('\n', lines);
        if (hasFinalNewLine && lines.Count > 0)
        {
            normalized += "\n";
        }
        var body = StrictUtf8.GetBytes(normalized);
        var bytes = hasBom
            ? Encoding.UTF8.Preamble.ToArray().Concat(body).ToArray()
            : body;
        using var stream = new MemoryStream(bytes, writable: false);
        var blob = repository.ObjectDatabase.CreateBlob(stream);
        var mode = indexEntry?.Mode ?? baselineMode ?? Mode.NonExecutableFile;
        repository.Index.Add(blob, path.Replace('\\', '/'), mode);
        repository.Index.Write();
    }

    private static void ApplyHunk(List<string> source, DiffHunk hunk, bool reverse)
    {
        if (hunk.Lines.Any(line => line.Origin == '\\'))
        {
            throw new InvalidOperationException(
                "该差异块包含文件末尾换行变化，无法在不改变其他内容的前提下安全处理，请改为暂存整个文件。");
        }
        var expected = hunk.Lines
            .Where(line => line.Origin == ' ' || line.Origin == (reverse ? '+' : '-'))
            .Select(line => line.Text)
            .ToArray();
        var replacement = hunk.Lines
            .Where(line => line.Origin == ' ' || line.Origin == (reverse ? '-' : '+'))
            .Select(line => line.Text)
            .ToArray();
        var oneBasedStart = reverse ? hunk.NewStart : hunk.OldStart;
        var offset = Math.Max(0, oneBasedStart - 1);
        if (offset == 0)
        {
            if (expected.Length > 0)
            {
                expected[0] = expected[0].TrimStart('\uFEFF');
            }
            if (replacement.Length > 0)
            {
                replacement[0] = replacement[0].TrimStart('\uFEFF');
            }
        }
        if (offset > source.Count || offset + expected.Length > source.Count ||
            !source.Skip(offset).Take(expected.Length).SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("差异块与当前暂存区内容不再匹配，请刷新后重试。");
        }
        source.RemoveRange(offset, expected.Length);
        source.InsertRange(offset, replacement);
    }

    private static byte[] ReadBlob(Repository repository, ObjectId id)
    {
        var blob = repository.Lookup<Blob>(id)
                   ?? throw new InvalidDataException("暂存区对象不存在。");
        using var source = blob.GetContentStream();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static string DecodeText(byte[] bytes, out bool hasBom, out bool hasFinalNewLine)
    {
        hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        hasFinalNewLine = EndsWithNewLine(bytes);
        var content = hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length).ToArray() : bytes;
        if (content.Contains((byte)0))
        {
            throw new InvalidOperationException("二进制文件不能按差异块暂存。");
        }
        try
        {
            return StrictUtf8.GetString(content)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("当前文件不是可安全处理的 UTF-8 文本，不能按差异块暂存。", exception);
        }
    }

    private static List<string> SplitLines(string text, bool hasFinalNewLine)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var lines = text.Split('\n').ToList();
        if (hasFinalNewLine && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    private static bool EndsWithNewLine(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 0 && (bytes[^1] == (byte)'\n' || bytes[^1] == (byte)'\r');

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            throw new ArgumentException("差异块文件路径无效。", nameof(path));
        }
    }
}
