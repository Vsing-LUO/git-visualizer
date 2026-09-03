namespace GitVisualizer.Infrastructure.FileSystem;

internal static class RepositoryPathGuard
{
    public static string EnsureSafe(
        string repositoryRoot,
        string path,
        bool includeTargetReparsePoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals(".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".git" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "文件操作必须位于当前仓库工作区内，且不能修改 .git 数据。");
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var count = includeTargetReparsePoint ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("文件操作不能穿过符号链接或目录联接。");
            }
        }
        return fullPath;
    }
}
