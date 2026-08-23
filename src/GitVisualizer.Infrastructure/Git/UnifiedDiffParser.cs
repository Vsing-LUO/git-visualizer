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
                if (line.Length > 0)
                {
                    switch (line[0])
                    {
                        case ' ':
                            diffLines.Add(new DiffLine(' ', currentOld++, currentNew++, line[1..]));
                            break;
                        case '-':
                            diffLines.Add(new DiffLine('-', currentOld++, null, line[1..]));
                            break;
                        case '+':
                            diffLines.Add(new DiffLine('+', null, currentNew++, line[1..]));
                            break;
                        case '\\':
                            diffLines.Add(new DiffLine('\\', null, null, line));
                            break;
                    }
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

    private static int ParseInt(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
