using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Git;

internal static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,(?<oldCount>\d+))? \+(?<new>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    public static IReadOnlyList<DiffHunk> Parse(
        string path, string patch, bool staged, string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var firstHunk = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunk < 0)
        {
            return [];
        }

        var fileHeader = string.Join('\n', lines[..firstHunk]) + "\n";
        var result = new List<DiffHunk>();
        for (var index = firstHunk; index < lines.Length;)
        {
            if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var match = HunkHeaderRegex().Match(lines[index]);
            if (!match.Success)
            {
                index++;
                continue;
            }

            var hunkStart = index;
            var header = lines[index++];
            var oldLine = ParseInt(match.Groups["old"].Value);
            var newLine = ParseInt(match.Groups["new"].Value);
            var oldCount = match.Groups["oldCount"].Success ? ParseInt(match.Groups["oldCount"].Value) : 1;
            var newCount = match.Groups["newCount"].Success ? ParseInt(match.Groups["newCount"].Value) : 1;
            var diffLines = new List<DiffLine>();
            var currentOld = oldLine;
            var currentNew = newLine;
            while (index < lines.Length && !lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var line = lines[index];
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    break;
                }
                if (line.Length == 0)
                {
                    index++;
                    continue;
                }

                var origin = line[0];
                switch (origin)
                {
                    case ' ':
                        diffLines.Add(new DiffLine(origin, currentOld++, currentNew++, line[1..]));
                        break;
                    case '-':
                        diffLines.Add(new DiffLine(origin, currentOld++, null, line[1..]));
                        break;
                    case '+':
                        diffLines.Add(new DiffLine(origin, null, currentNew++, line[1..]));
                        break;
                    case '\\':
                        diffLines.Add(new DiffLine(origin, null, null, line));
                        break;
                }
                index++;
            }

            var hunkPatch = fileHeader + string.Join('\n', lines[hunkStart..index]) + "\n";
            var idMaterial = $"{path}\0{header}\0{hunkPatch}\0{snapshotId}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idMaterial)))[..16];
            result.Add(new DiffHunk(
                id, path, header, oldLine, oldCount, newLine, newCount,
                diffLines, hunkPatch, snapshotId, staged));
        }

        return result;
    }

    public static string CombinePatches(IReadOnlyList<DiffHunk> hunks)
    {
        if (hunks.Count == 0)
        {
            throw new ArgumentException("至少选择一个差异块。", nameof(hunks));
        }

        var first = hunks[0].Patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var firstHunk = first.IndexOf("@@ ", StringComparison.Ordinal);
        if (firstHunk < 0)
        {
            throw new InvalidDataException("差异块缺少统一补丁头。");
        }

        var builder = new StringBuilder(first[..firstHunk]);
        foreach (var hunk in hunks)
        {
            var patch = hunk.Patch.Replace("\r\n", "\n", StringComparison.Ordinal);
            var index = patch.IndexOf("@@ ", StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidDataException("差异块格式无效。");
            }
            builder.Append(patch[index..]);
            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append('\n');
            }
        }
        return builder.ToString();
    }

    public static string ReversePatch(string patch)
    {
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                result.Add("+++ " + line[4..]);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                result.Add("--- " + line[4..]);
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                {
                    result.Add(line);
                    continue;
                }
                var suffix = line[match.Length..];
                var oldStart = match.Groups["old"].Value;
                var oldCount = match.Groups["oldCount"].Success ? "," + match.Groups["oldCount"].Value : string.Empty;
                var newStart = match.Groups["new"].Value;
                var newCount = match.Groups["newCount"].Success ? "," + match.Groups["newCount"].Value : string.Empty;
                result.Add($"@@ -{newStart}{newCount} +{oldStart}{oldCount} @@{suffix}");
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                result.Add("-" + line[1..]);
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                result.Add("+" + line[1..]);
            }
            else
            {
                result.Add(line);
            }
        }
        return string.Join('\n', result);
    }

    private static int ParseInt(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
