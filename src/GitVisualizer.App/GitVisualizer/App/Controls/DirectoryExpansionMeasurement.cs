// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Infrastructure.Diagnostics;

namespace GitVisualizer.App.Controls;

internal static class DirectoryExpansionMeasurement
{
    internal static void Attach(TreeView tree)
    {
        tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is not TreeViewItem { DataContext: FileTreeItem { IsDirectory: true } }) return;
            var measurement = PerformanceRecorder.Begin(PerformanceOperation.DirectoryExpand);
            if (measurement is null) return;
            // Current tree is eagerly populated. Measure expansion through queued layout/render work.
            var pending = tree.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(measurement.Dispose));
            pending.Aborted += (_, _) => measurement.Dispose();
        }), true);
    }
}
