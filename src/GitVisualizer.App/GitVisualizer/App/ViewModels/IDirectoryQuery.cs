// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.IO;

namespace GitVisualizer.App.ViewModels;

internal interface IDirectoryQuery
{
    Task<FileTreeItem[]> ReadChildrenAsync(string directory, CancellationToken token);
}

internal sealed class DirectoryQuery : IDirectoryQuery
{
    internal static IDirectoryQuery Default { get; } = new DirectoryQuery();

    public Task<FileTreeItem[]> ReadChildrenAsync(string directory, CancellationToken token) => Task.Run(() =>
    {
        // Never enumerate a link target: this also rejects cycles and repository escapes.
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("链接目录请使用外部程序打开。");
        return Directory.EnumerateFileSystemEntries(directory)
            .Select(path => { token.ThrowIfCancellationRequested(); return path; })
            .Where(path => !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)
                && !FileTreeItem.IsTransientOfficeLockFile(path))
            .Select(path => FileTreeItem.Create(path, this))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }, token);
}
