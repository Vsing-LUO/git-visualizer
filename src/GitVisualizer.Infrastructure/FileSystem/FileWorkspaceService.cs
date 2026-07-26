using System.Text;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.FileSystem;

public sealed class FileWorkspaceService : IFileWorkspaceService
{
    private const long MaxEditableSize = 5 * 1024 * 1024;

    public async Task<TextDocument> OpenTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var isBinary = IsBinary(bytes);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = isBinary ? string.Empty : encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        return new TextDocument(
            path,
            text,
            encoding.WebName,
            newLine,
            info.LastWriteTimeUtc,
            info.IsReadOnly || info.Length > MaxEditableSize || isBinary,
            isBinary,
            info.Length);
    }

    public async Task SaveTextAsync(
        TextDocument original,
        string text,
        bool allowExternalOverwrite,
        CancellationToken cancellationToken = default)
    {
        if (original.IsReadOnly)
        {
            throw new InvalidOperationException("此文件当前为只读。");
        }

        var info = new FileInfo(original.Path);
        if (info.Exists && !allowExternalOverwrite &&
            info.LastWriteTimeUtc != original.LastWriteTime.UtcDateTime)
        {
            throw new IOException("文件已被外部程序修改，请重新加载或确认覆盖。");
        }

        var encoding = Encoding.GetEncoding(original.EncodingName);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", original.NewLine, StringComparison.Ordinal);
        var directory = Path.GetDirectoryName(original.Path)
                        ?? throw new InvalidOperationException("无效的文件路径。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(original.Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, normalized, encoding, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, original.Path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public Task CreateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("目标已经存在。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)
                                  ?? throw new InvalidOperationException("无效的文件路径。"));
        using var _ = File.Create(path);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("目标已经存在。");
        }

        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("目标已经存在，未覆盖任何内容。");
        }

        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            throw new FileNotFoundException("源路径不存在。", source);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        return Task.CompletedTask;
    }

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

    private static bool IsBinary(byte[] bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < sampleLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
