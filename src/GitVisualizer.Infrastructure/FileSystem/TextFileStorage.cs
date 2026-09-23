// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.FileSystem;

/// <summary>Strict text decoding and single-file, optimistic safe saves.</summary>
public static class TextFileStorage
{
    public const int EditLimit = 5 * 1024 * 1024;
    public const int SampleLimit = 64 * 1024;

    public static TextDocument Oversized(string path, long size, DateTimeOffset modified = default) =>
        new(path, string.Empty, "utf-8", "\n", modified, true, false, size,
            ReadOnlyReason: "文件超过 5 MiB 编辑上限，请使用外部程序打开。");

    public static TextDocument Decode(string path, byte[] bytes, DateTimeOffset modified = default,
        bool readOnly = false)
    {
        if (bytes.LongLength > EditLimit) return Oversized(path, bytes.LongLength, modified);
        Encoding encoding = new UTF8Encoding(false, true);
        int bomLength = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bomLength = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            bomLength = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            bomLength = 2;
        }
        string text = string.Empty;
        string? reason = null;
        bool binary = false;
        try
        {
            text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            binary = text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f'));
            if (binary) { text = string.Empty; reason = "二进制文件，请使用外部程序打开。"; }
        }
        catch (DecoderFallbackException)
        {
            binary = true;
            reason = "无法严格解码为 UTF-8 或带 BOM 的 UTF-16，请使用外部程序打开。";
        }
        var kinds = new HashSet<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { kinds.Add("\r\n"); i++; }
                else kinds.Add("\r");
            }
            else if (text[i] == '\n') kinds.Add("\n");
        }
        bool mixed = kinds.Count > 1;
        reason ??= mixed ? "混合换行文件为只读，请使用外部程序打开。" :
            readOnly ? "文件为只读。" : bytes.LongLength > 5 * 1024 * 1024 ? "文件过大，当前为只读。" : null;
        return new TextDocument(path, text, encoding.WebName, kinds.FirstOrDefault() ?? "\n",
            modified, reason != null, binary, bytes.LongLength,
            HasBom: bomLength != 0, HasTrailingNewLine: text.EndsWith('\n') || text.EndsWith('\r'),
            HasMixedNewLines: mixed, OriginalByteDigest: Convert.ToHexString(SHA256.HashData(bytes)),
            ReadOnlyReason: reason);
    }

    public static async Task<TextDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = stream.Length;
        var info = new FileInfo(path);
        if (size > EditLimit) return Oversized(path, size, info.LastWriteTimeUtc);
        var bytes = new byte[checked((int)size)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Decode(path, bytes, info.LastWriteTimeUtc, info.IsReadOnly);
    }

    public static Task SaveAsync(string repositoryRoot, TextDocument original, string text,
        CancellationToken cancellationToken = default) =>
        Git.RepositoryWriteLock.RunAsync(repositoryRoot,
            () => SaveCoreAsync(repositoryRoot, original, text, new TextFileOperations(), cancellationToken), cancellationToken);

    internal static async Task SaveCoreAsync(string repositoryRoot, TextDocument original, string text,
        TextFileOperations operations, CancellationToken cancellationToken = default, Action? validateAdditionalState = null)
    {
        if (original.IsReadOnly || original.IsBinary || original.HasMixedNewLines || original.OriginalByteDigest is null)
            throw new InvalidOperationException(original.ReadOnlyReason ?? "文件缺少安全保存所需的原始快照，请重新打开。");
        (DateTime Written, DateTime Created, FileAttributes Attributes)? checkedState = null;
        await CheckAsync().ConfigureAwait(false);
        // No-op saves never rewrite the file, including its original BOM and line endings.
        if (string.Equals(text, original.Text, StringComparison.Ordinal)) return;
        Encoding encoding = original.EncodingName switch
        {
            "utf-8" => new UTF8Encoding(original.HasBom, true),
            "utf-16" => new UnicodeEncoding(false, original.HasBom, true),
            "utf-16BE" => new UnicodeEncoding(true, original.HasBom, true),
            _ => throw new InvalidOperationException("不支持安全写入此编码。")
        };
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace("\n", original.NewLine, StringComparison.Ordinal);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(normalized)).ToArray();
        var directory = Path.GetDirectoryName(Path.GetFullPath(original.Path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(original.Path)}.{Guid.NewGuid():N}.tmp");
        Exception? failure = null;
        var retainTemporary = false;
        try
        {
            await operations.WriteAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            await CheckAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            operations.Replace(temporary, original.Path);
        }
        catch (IOException error) when (error.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
        {
            // ReplaceFileW errors 1176/1177 may leave the original missing or renamed.
            // Do not delete the only remaining draft, or overwrite a path another process now owns.
            retainTemporary = true;
            failure = new IncompleteReplacementException(temporary, error);
            throw failure;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            if (!retainTemporary && File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception cleanupError) when (failure is not null &&
                    cleanupError is IOException or UnauthorizedAccessException)
                {
                    // Preserve the primary error/HResult if a scanner or another process
                    // has the temporary file open. Never force-delete or change its permissions.
                    failure.Data["TemporaryCleanupError"] = cleanupError;
                    failure.Data["RetainedTemporaryPath"] = temporary;
                }
            }
        }

        async Task CheckAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepositoryPathGuard.EnsureSafe(repositoryRoot, original.Path, includeTargetReparsePoint: true);
            if (!File.Exists(original.Path)) throw new ExternalFileChangedException(original.Path);
            var info = new FileInfo(original.Path);
            var state = (info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
            if (checkedState is not null && checkedState.Value != state)
                throw new ExternalFileChangedException(original.Path);
            checkedState = state;
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("文件为只读，未保存修改。");
            await using var current = new FileStream(original.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (Convert.ToHexString(await SHA256.HashDataAsync(current, cancellationToken).ConfigureAwait(false)) != original.OriginalByteDigest)
                throw new ExternalFileChangedException(original.Path);
            validateAdditionalState?.Invoke();
        }
    }
    private sealed class IncompleteReplacementException : IOException
    {
        internal IncompleteReplacementException(string temporary, IOException inner)
            : base($"原子替换未完成（Windows 错误 {inner.HResult & 0xffff}）。请核对目标文件；临时副本已保留供恢复：{temporary}", inner)
        {
            HResult = inner.HResult;
            Data["RetainedTemporaryPath"] = temporary;
        }
    }
}

// Fault-injection boundary used by tests; production always flushes and uses same-volume replacement.
internal class TextFileOperations
{
    internal virtual async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    internal virtual void Replace(string temporary, string target) => File.Replace(temporary, target, null);
}
