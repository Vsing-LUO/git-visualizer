using System.Text;
using System.Text.RegularExpressions;

namespace GitVisualizer.Infrastructure.Git;

internal static partial class GitPatchDisplayFormatter
{
    private static readonly string[] PathHeaderPrefixes =
    [
        "diff --git ",
        "--- ",
        "+++ ",
        "rename from ",
        "rename to ",
        "copy from ",
        "copy to ",
        "Binary files "
    ];

    [GeneratedRegex("\"(?<content>(?:\\\\.|[^\"\\\\])*)\"")]
    private static partial Regex QuotedPathRegex();

    public static string Format(string patch, string path, bool staged)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return string.Empty;
        }

        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains("Binary files ", StringComparison.Ordinal) ||
            normalized.Contains("GIT binary patch", StringComparison.Ordinal))
        {
            return
                "这是二进制文件，无法显示逐行文本差异。\n\n" +
                $"文件：{path}\n" +
                $"区域：{(staged ? "已暂存" : "未暂存")}\n\n" +
                "该文件仍可正常暂存和提交。";
        }

        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (PathHeaderPrefixes.Any(
                    prefix => lines[index].StartsWith(prefix, StringComparison.Ordinal)))
            {
                lines[index] = DecodeQuotedPaths(lines[index]);
            }
        }

        return string.Join('\n', lines);
    }

    public static string FormatCommitComparison(
        string patch,
        string oldCommitId,
        string newCommitId)
    {
        var oldShortId = oldCommitId[..Math.Min(8, oldCommitId.Length)];
        var newShortId = newCommitId[..Math.Min(8, newCommitId.Length)];
        if (string.IsNullOrWhiteSpace(patch))
        {
            return $"{oldShortId} → {newShortId}\n\n两个提交的文件内容相同。";
        }

        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (PathHeaderPrefixes.Any(
                    prefix => lines[index].StartsWith(prefix, StringComparison.Ordinal)))
            {
                lines[index] = DecodeQuotedPaths(lines[index]);
            }
        }

        return $"比较提交：{oldShortId} → {newShortId}\n\n{string.Join('\n', lines)}";
    }

    public static string FormatRaw(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return string.Empty;
        }

        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (PathHeaderPrefixes.Any(
                    prefix => lines[index].StartsWith(prefix, StringComparison.Ordinal)))
            {
                lines[index] = DecodeQuotedPaths(lines[index]);
            }
        }
        return string.Join('\n', lines);
    }

    internal static string DecodePathValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? DecodeGitQuotedString(trimmed[1..^1])
            : trimmed;
    }

    private static string DecodeQuotedPaths(string line) =>
        QuotedPathRegex().Replace(
            line,
            match => $"\"{DecodeGitQuotedString(match.Groups["content"].Value)}\"");

    internal static string DecodeGitQuotedString(string value)
    {
        var result = new StringBuilder(value.Length);
        var escapedBytes = new List<byte>();

        void FlushBytes()
        {
            if (escapedBytes.Count == 0)
            {
                return;
            }
            result.Append(Encoding.UTF8.GetString([.. escapedBytes]));
            escapedBytes.Clear();
        }

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\')
            {
                FlushBytes();
                result.Append(current);
                continue;
            }

            if (index + 1 >= value.Length)
            {
                FlushBytes();
                result.Append('\\');
                break;
            }

            var next = value[++index];
            if (next is >= '0' and <= '7' &&
                index + 2 < value.Length &&
                value[index + 1] is >= '0' and <= '7' &&
                value[index + 2] is >= '0' and <= '7')
            {
                var octal =
                    ((next - '0') << 6) |
                    ((value[index + 1] - '0') << 3) |
                    (value[index + 2] - '0');
                escapedBytes.Add((byte)octal);
                index += 2;
                continue;
            }

            FlushBytes();
            result.Append(next switch
            {
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                '\\' => '\\',
                '"' => '"',
                _ => next
            });
        }

        FlushBytes();
        return result.ToString();
    }
}
