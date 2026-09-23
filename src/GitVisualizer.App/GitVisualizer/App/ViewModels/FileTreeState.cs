// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using GitVisualizer.Core;

namespace GitVisualizer.App.ViewModels;

// Window-scoped state; the coordinator exposes notifications without keeping another copy.
internal sealed class FileTreeState
{
    internal int FileTreeLoadVersion { get; set; }
    internal bool IsBrowsingHistoricalCommit { get; set; }
    internal bool CanModifyFileTree { get; set; }
    internal string FileTreeContextText { get; set; } = "工作区";
    internal ObservableCollection<FileTreeItem> FileTree { get; } = new();

    internal int BeginLoad() => ++FileTreeLoadVersion;
    internal bool IsCurrent(int version) => version == FileTreeLoadVersion;
    internal void Invalidate() => ++FileTreeLoadVersion;
}
