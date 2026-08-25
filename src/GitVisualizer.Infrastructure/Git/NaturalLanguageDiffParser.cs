using System.Globalization;
using System.Text.RegularExpressions;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Git;

internal static partial class NaturalLanguageDiffParser
{
    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,(?<oldCount>\d+))? \+(?<new>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    [GeneratedRegex("^diff --git (?<old>\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|\\S+) (?<new>\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|\\S+)$")]
    private static partial Regex DiffHeaderRegex();

    public static DiffPresentation Parse(
        string patch,
        string title,
        string oldLabel,
        string newLabel,
        IReadOnlyList<DiffHunk>? sourceHunks = null,
        string? fallbackPath = null)
    {
        var rawText = GitPatchDisplayFormatter.FormatRaw(patch);
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new DiffPresentation(
                title, "没有发现内容差异。", oldLabel, newLabel, [], rawText);
        }

        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var files = new List<DiffFilePresentation>();
        var sourceIndex = 0;
        var index = 0;
        while (index < lines.Length)
        {
            while (index < lines.Length &&
                   !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                index++;
            }
            if (index >= lines.Length)
            {
                break;
            }

            var diffHeader = lines[index++];
            var headerMatch = DiffHeaderRegex().Match(diffHeader);
            string? oldPath = headerMatch.Success
                ? ParseMarkerPath(headerMatch.Groups["old"].Value)
                : null;
            string? newPath = headerMatch.Success
                ? ParseMarkerPath(headerMatch.Groups["new"].Value)
                : null;
            string? renameFrom = null;
            string? renameTo = null;
            string? copyFrom = null;
            string? copyTo = null;
            var isAdded = false;
            var isDeleted = false;
            var isBinary = false;
            var hasUnknownMetadata = false;

            while (index < lines.Length &&
                   !lines[index].StartsWith("@@ ", StringComparison.Ordinal) &&
                   !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var line = lines[index];
                if (line.StartsWith("new file mode ", StringComparison.Ordinal))
                {
                    isAdded = true;
                }
                else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
                {
                    isDeleted = true;
                }
                else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    renameFrom = ParsePathValue(line[12..]);
                }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    renameTo = ParsePathValue(line[10..]);
                }
                else if (line.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    copyFrom = ParsePathValue(line[10..]);
                }
                else if (line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    copyTo = ParsePathValue(line[8..]);
                }
                else if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    oldPath = ParseMarkerPath(line[4..]);
                }
                else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    newPath = ParseMarkerPath(line[4..]);
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                         line.Equals("GIT binary patch", StringComparison.Ordinal))
                {
                    isBinary = true;
                    if (line.StartsWith("Binary files ", StringComparison.Ordinal) &&
                        line.EndsWith(" differ", StringComparison.Ordinal))
                    {
                        var paths = line[13..^7];
                        var separator = paths.IndexOf(" and ", StringComparison.Ordinal);
                        if (separator >= 0)
                        {
                            oldPath = ParseMarkerPath(paths[..separator]);
                            newPath = ParseMarkerPath(paths[(separator + 5)..]);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line) &&
                         !line.StartsWith("index ", StringComparison.Ordinal) &&
                         !line.StartsWith("similarity index ", StringComparison.Ordinal) &&
                         !line.StartsWith("dissimilarity index ", StringComparison.Ordinal))
                {
                    hasUnknownMetadata = true;
                }
                index++;
            }

            oldPath = renameFrom ?? copyFrom ?? oldPath;
            newPath = renameTo ?? copyTo ?? newPath;
            var regions = new List<DiffRegionPresentation>();
            var notices = new List<string>();
            var oldNoNewline = false;
            var newNoNewline = false;

            while (index < lines.Length &&
                   !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
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

                var oldStart = ParseInt(match.Groups["old"].Value);
                var newStart = ParseInt(match.Groups["new"].Value);
                var oldCount = match.Groups["oldCount"].Success
                    ? ParseInt(match.Groups["oldCount"].Value)
                    : 1;
                var newCount = match.Groups["newCount"].Success
                    ? ParseInt(match.Groups["newCount"].Value)
                    : 1;
                index++;
                var oldLine = oldStart;
                var newLine = newStart;
                var parsedLines = new List<ParsedLine>();
                ParsedLine? previous = null;
                while (index < lines.Length &&
                       !lines[index].StartsWith("@@ ", StringComparison.Ordinal) &&
                       !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    var line = lines[index];
                    if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                    {
                        if (previous?.Origin == '-')
                        {
                            oldNoNewline = true;
                        }
                        else if (previous?.Origin == '+')
                        {
                            newNoNewline = true;
                        }
                        else if (previous?.Origin == ' ')
                        {
                            oldNoNewline = true;
                            newNoNewline = true;
                        }
                        index++;
                        continue;
                    }
                    if (line.Length == 0 || line[0] is not (' ' or '-' or '+'))
                    {
                        index++;
                        continue;
                    }

                    previous = line[0] switch
                    {
                        ' ' => new ParsedLine(' ', oldLine++, newLine++, line[1..]),
                        '-' => new ParsedLine('-', oldLine++, null, line[1..]),
                        '+' => new ParsedLine('+', null, newLine++, line[1..]),
                        _ => throw new InvalidOperationException()
                    };
                    parsedLines.Add(previous);
                    index++;
                }

                var blocks = BuildBlocks(parsedLines);
                var sourceHunk = sourceIndex < (sourceHunks?.Count ?? 0)
                    ? sourceHunks![sourceIndex]
                    : null;
                sourceIndex++;
                regions.Add(new DiffRegionPresentation(
                    sourceHunk?.Id ?? $"region-{files.Count + 1}-{regions.Count + 1}",
                    DescribeLocation(oldStart, oldCount, newStart, newCount),
                    oldLabel,
                    newLabel,
                    blocks,
                    sourceHunk));
            }

            if (oldNoNewline != newNoNewline)
            {
                notices.Add(oldNoNewline
                    ? "修改后补上了文件末尾的换行符。"
                    : "修改后移除了文件末尾的换行符。");
                RemoveNewlineOnlyBlocks(regions);
            }
            if (hasUnknownMetadata && regions.Count == 0 && !isBinary)
            {
                notices.Add("这个文件还有无法归类的属性变化，可切换到原始差异核对。");
            }

            var kind = renameFrom is not null || renameTo is not null
                ? DiffFileChangeKind.Renamed
                : copyFrom is not null || copyTo is not null
                    ? DiffFileChangeKind.Copied
                    : isAdded || oldPath is null
                        ? DiffFileChangeKind.Added
                        : isDeleted || newPath is null
                            ? DiffFileChangeKind.Deleted
                            : DiffFileChangeKind.Modified;
            var path = newPath ?? oldPath ?? fallbackPath ?? "未知文件";
            oldPath ??= kind == DiffFileChangeKind.Added ? null : path;
            var statusText = DescribeStatus(kind, isBinary);
            var summary = BuildFileSummary(kind, isBinary, regions, notices);
            files.Add(new DiffFilePresentation(
                path,
                oldPath,
                kind,
                statusText,
                summary,
                oldLabel,
                newLabel,
                isBinary,
                regions,
                notices));
        }

        if (files.Count == 0 && fallbackPath is not null)
        {
            files.Add(new DiffFilePresentation(
                fallbackPath, fallbackPath, DiffFileChangeKind.Unknown,
                "文件属性发生变化", "Git 没有提供可逐行展示的内容。",
                oldLabel, newLabel, false, [],
                ["可切换到原始差异核对详细信息。"]));
        }

        return new DiffPresentation(
            title,
            BuildDocumentSummary(files),
            oldLabel,
            newLabel,
            files,
            rawText);
    }

    private static IReadOnlyList<DiffChangeBlock> BuildBlocks(IReadOnlyList<ParsedLine> lines)
    {
        var blocks = new List<DiffChangeBlock>();
        var index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Origin == ' ')
            {
                var context = TakeRun(lines, ref index, ' ');
                blocks.Add(new DiffChangeBlock(
                    DiffChangeBlockKind.Context,
                    "未修改的上下文",
                    "这些内容没有变化，仅用于帮助定位。",
                    context.Select(line => new DiffLinePair(
                        ToDisplayLine(line.OldLine, line.Text),
                        ToDisplayLine(line.NewLine, line.Text),
                        IsContext: true)).ToArray()));
                continue;
            }
            if (lines[index].Origin == '-')
            {
                var removed = TakeRun(lines, ref index, '-');
                var added = index < lines.Count && lines[index].Origin == '+'
                    ? TakeRun(lines, ref index, '+')
                    : [];
                if (added.Count > 0)
                {
                    var rows = new List<DiffLinePair>();
                    for (var row = 0; row < Math.Max(removed.Count, added.Count); row++)
                    {
                        var oldItem = row < removed.Count ? removed[row] : null;
                        var newItem = row < added.Count ? added[row] : null;
                        var whitespaceOnly = oldItem is not null && newItem is not null &&
                                             IsWhitespaceOnlyChange(oldItem.Text, newItem.Text);
                        rows.Add(new DiffLinePair(
                            oldItem is null ? null : ToDisplayLine(oldItem.OldLine, oldItem.Text, whitespaceOnly),
                            newItem is null ? null : ToDisplayLine(newItem.NewLine, newItem.Text, whitespaceOnly)));
                    }
                    blocks.Add(new DiffChangeBlock(
                        DiffChangeBlockKind.Modified,
                        "修改内容",
                        $"将修改前的 {removed.Count} 行调整为修改后的 {added.Count} 行。",
                        rows));
                }
                else
                {
                    blocks.Add(new DiffChangeBlock(
                        DiffChangeBlockKind.Deleted,
                        "删除内容",
                        $"修改后不再包含下面 {removed.Count} 行。",
                        removed.Select(line => new DiffLinePair(
                            ToDisplayLine(line.OldLine, line.Text), null)).ToArray()));
                }
                continue;
            }

            var additions = TakeRun(lines, ref index, '+');
            blocks.Add(new DiffChangeBlock(
                DiffChangeBlockKind.Added,
                "新增内容",
                $"修改后增加了下面 {additions.Count} 行。",
                additions.Select(line => new DiffLinePair(
                    null, ToDisplayLine(line.NewLine, line.Text))).ToArray()));
        }
        return blocks;
    }

    private static List<ParsedLine> TakeRun(
        IReadOnlyList<ParsedLine> lines, ref int index, char origin)
    {
        var result = new List<ParsedLine>();
        while (index < lines.Count && lines[index].Origin == origin)
        {
            result.Add(lines[index++]);
        }
        return result;
    }

    private static DiffDisplayLine ToDisplayLine(
        int? lineNumber, string text, bool visualizeWhitespace = false) =>
        new(lineNumber, text, visualizeWhitespace);

    private static bool IsWhitespaceOnlyChange(string oldText, string newText) =>
        !oldText.Equals(newText, StringComparison.Ordinal) &&
        RemoveWhitespace(oldText).Equals(RemoveWhitespace(newText), StringComparison.Ordinal);

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static void RemoveNewlineOnlyBlocks(List<DiffRegionPresentation> regions)
    {
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            var filtered = region.Blocks.Where(block =>
                block.Kind != DiffChangeBlockKind.Modified ||
                block.Rows.Any(row => !string.Equals(
                    row.OldLine?.Text,
                    row.NewLine?.Text,
                    StringComparison.Ordinal))).ToArray();
            regions[index] = region with { Blocks = filtered };
        }
    }

    private static string DescribeLocation(int oldStart, int oldCount, int newStart, int newCount) =>
        $"修改位置：{DescribeRange("修改前", oldStart, oldCount)}；{DescribeRange("修改后", newStart, newCount)}";

    private static string DescribeRange(string label, int start, int count) => count switch
    {
        0 => $"{label}在第 {start} 行附近没有对应内容",
        1 => $"{label}第 {start} 行",
        _ => $"{label}第 {start}–{start + count - 1} 行"
    };

    private static string BuildFileSummary(
        DiffFileChangeKind kind,
        bool isBinary,
        IReadOnlyList<DiffRegionPresentation> regions,
        IReadOnlyList<string> notices)
    {
        if (isBinary)
        {
            return "二进制内容发生变化，无法像文本一样逐行比较。";
        }
        var blocks = regions.SelectMany(region => region.Blocks).ToArray();
        var modified = blocks.Count(block => block.Kind == DiffChangeBlockKind.Modified);
        var added = blocks.Where(block => block.Kind == DiffChangeBlockKind.Added)
            .Sum(block => block.Rows.Count(row => row.NewLine is not null));
        var deleted = blocks.Where(block => block.Kind == DiffChangeBlockKind.Deleted)
            .Sum(block => block.Rows.Count(row => row.OldLine is not null));
        var details = new List<string>();
        if (modified > 0) details.Add($"修改 {modified} 处");
        if (added > 0) details.Add($"新增 {added} 行");
        if (deleted > 0) details.Add($"删除 {deleted} 行");
        if (details.Count == 0 && notices.Count > 0) details.Add("文件属性发生变化");
        var prefix = kind switch
        {
            DiffFileChangeKind.Added => "新建文件",
            DiffFileChangeKind.Deleted => "删除文件",
            DiffFileChangeKind.Renamed => "重命名文件",
            DiffFileChangeKind.Copied => "复制文件",
            _ => "修改文件"
        };
        return details.Count == 0 ? prefix : $"{prefix}：{string.Join("，", details)}。";
    }

    private static string BuildDocumentSummary(IReadOnlyList<DiffFilePresentation> files)
    {
        if (files.Count == 0)
        {
            return "没有发现内容差异。";
        }
        var descriptions = new List<string>();
        AddCount(DiffFileChangeKind.Modified, "修改");
        AddCount(DiffFileChangeKind.Added, "新建");
        AddCount(DiffFileChangeKind.Deleted, "删除");
        AddCount(DiffFileChangeKind.Renamed, "重命名");
        AddCount(DiffFileChangeKind.Copied, "复制");
        AddCount(DiffFileChangeKind.Unknown, "属性变化");
        var regionCount = files.Sum(file => file.Regions.Count);
        var regionText = regionCount == 0 ? string.Empty : $"，共 {regionCount} 个变化区域";
        return $"共 {files.Count} 个文件有变化{regionText}：{string.Join("，", descriptions)}。";

        void AddCount(DiffFileChangeKind kind, string label)
        {
            var count = files.Count(file => file.ChangeKind == kind);
            if (count > 0)
            {
                descriptions.Add($"{label} {count} 个");
            }
        }
    }

    private static string DescribeStatus(DiffFileChangeKind kind, bool isBinary)
    {
        var status = kind switch
        {
            DiffFileChangeKind.Added => "新建文件",
            DiffFileChangeKind.Deleted => "文件已删除",
            DiffFileChangeKind.Renamed => "文件已重命名",
            DiffFileChangeKind.Copied => "复制得到的新文件",
            DiffFileChangeKind.Unknown => "文件属性发生变化",
            _ => "文件内容已修改"
        };
        return isBinary ? $"{status} · 二进制文件" : status;
    }

    private static string? ParseMarkerPath(string value)
    {
        var path = ParsePathValue(value);
        if (path.Equals("/dev/null", StringComparison.Ordinal))
        {
            return null;
        }
        return path.StartsWith("a/", StringComparison.Ordinal) ||
               path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
    }

    private static string ParsePathValue(string value) =>
        GitPatchDisplayFormatter.DecodePathValue(value);

    private static int ParseInt(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private sealed record ParsedLine(char Origin, int? OldLine, int? NewLine, string Text);
}
