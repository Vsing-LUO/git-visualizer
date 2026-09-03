using GitVisualizer.App;
using GitVisualizer.App.Dialogs;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using System.Windows;
using System.Windows.Threading;

namespace GitVisualizer.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(MessageBoxButton.OK, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNo, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNoCancel, MessageBoxResult.Cancel)]
    public void ThemedMessageBoxCloseBehaviorMatchesButtonSet(
        MessageBoxButton buttons,
        MessageBoxResult expected)
    {
        Assert.Equal(expected, ThemedMessageBoxWindow.GetCloseResult(buttons));
    }

    [Fact]
    public void FileWorkspaceDefaultsToMidpointBetweenScreenshotLimits()
    {
        Assert.Equal(180, MainWindow.MinimumFileWorkspaceHeight);
        Assert.Equal(280, MainWindow.MaximumFileWorkspaceHeight);
        Assert.Equal(
            (MainWindow.MinimumFileWorkspaceHeight +
             MainWindow.MaximumFileWorkspaceHeight) / 2,
            MainWindow.DefaultFileWorkspaceHeight);
    }

    [Fact]
    public void FileWorkspaceMaximumStillLeavesThreeBranchesVisible()
    {
        Assert.True(
            MainWindow.MinimumBranchListHeight >=
            MainWindow.BranchItemHeight * MainWindow.MinimumVisibleBranchCount);
    }

    [Fact]
    public void DefaultStagingHeightTargetsTheCompactLayoutBoundary()
    {
        const double fixedRowsHeight = 82;
        var boundaryHeight =
            MainWindow.CalculateCompactBoundaryPanelHeight(fixedRowsHeight);

        Assert.Equal(151, boundaryHeight);
        Assert.True(MainWindow.ShouldUseCompactCommitMessageLayout(
            boundaryHeight,
            boundaryHeight - fixedRowsHeight,
            true));
        Assert.False(MainWindow.ShouldUseCompactCommitMessageLayout(
            boundaryHeight + 1,
            boundaryHeight + 1 - fixedRowsHeight,
            true));
    }

    [Theory]
    [InlineData(200, 78, false, true)]
    [InlineData(200, 82, false, false)]
    [InlineData(200, 88, true, true)]
    [InlineData(200, 94, true, false)]
    public void CommitMessageLayoutUsesFortyPercentThresholdWithHysteresis(
        double panelHeight,
        double messageRowHeight,
        bool isCurrentlyCompact,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldUseCompactCommitMessageLayout(
                panelHeight,
                messageRowHeight,
                isCurrentlyCompact));
    }

    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(199, 199, false)]
    [InlineData(200, 200, false)]
    [InlineData(201, 200, true)]
    public void HistoryPageUsesOneLookaheadCommitToDetectMoreHistory(
        int fetchedCount,
        int expectedVisibleCount,
        bool expectedHasMore)
    {
        var state = MainWindowViewModel.CalculateHistoryPageState(fetchedCount);

        Assert.Equal(expectedVisibleCount, state.VisibleCount);
        Assert.Equal(expectedHasMore, state.HasMore);
    }

    [Fact]
    public void FileOperationUsesOnlyAvailableItemWhenNothingIsSelected()
    {
        var target = new object();

        var result = MainWindow.ResolveOperationTargets<object>([], [target]);

        Assert.Equal([target], result);
    }

    [Fact]
    public void FileOperationStillRequiresSelectionWhenSeveralItemsAreAvailable()
    {
        var result = MainWindow.ResolveOperationTargets<object>([], [new(), new()]);

        Assert.Empty(result);
    }

    [Fact]
    public void FileOperationPrefersExplicitSelection()
    {
        var selected = new object();

        var result = MainWindow.ResolveOperationTargets(
            [selected],
            new object[] { new(), selected, new() });

        Assert.Equal([selected], result);
    }

    [Fact]
    public void ConflictSelectionSurvivesCollectionReplacementByPath()
    {
        var first = new ConflictFile(
            "first.txt", "base", "ours", "theirs", "result", false, false);
        var replacement = new ConflictFile(
            "FIRST.txt", "new base", "new ours", "new theirs", "new result", false, false);
        var fallback = new ConflictFile(
            "second.txt", "", "", "", "", false, false);

        Assert.Same(
            replacement,
            MainWindow.ResolveConflictSelection("first.txt", [replacement, fallback]));
        Assert.Same(
            replacement,
            MainWindow.ResolveConflictSelection("missing.txt", [replacement, fallback]));
        Assert.Null(MainWindow.ResolveConflictSelection("first.txt", []));
    }

    [Fact]
    public void RepositoryTerminalStartsPowerShellInNormalizedRepositoryDirectory()
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            "GitVisualizer.Tests",
            "包含 空格的仓库",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var startInfo = MainWindow.CreateRepositoryTerminalStartInfo(repositoryPath);

            Assert.Equal("powershell.exe", startInfo.FileName);
            Assert.Equal(Path.GetFullPath(repositoryPath), startInfo.WorkingDirectory);
            Assert.True(startInfo.UseShellExecute);
            Assert.Empty(startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public void RepositoryTerminalRejectsMissingDirectory()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "GitVisualizer.Tests",
            Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(
            () => MainWindow.CreateRepositoryTerminalStartInfo(missingPath));
    }

    [Fact]
    public void CloseGuardRunsAfterWpfClosingStateAndCanShowOwnedDialog()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            var allowClose = false;
            var owner = new Window
            {
                Width = 200,
                Height = 120,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            owner.Closing += (_, args) =>
            {
                if (allowClose)
                {
                    return;
                }
                args.Cancel = true;
                MainWindow.DeferCloseGuard(owner.Dispatcher, () =>
                {
                    try
                    {
                        var dialog = new Window
                        {
                            Owner = owner,
                            Width = 160,
                            Height = 90,
                            ShowInTaskbar = false,
                            WindowStyle = WindowStyle.ToolWindow
                        };
                        dialog.Loaded += (_, _) => dialog.Close();
                        dialog.ShowDialog();
                        completed = true;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    return Task.CompletedTask;
                });
            };

            owner.Show();
            owner.Close();
            var frame = new DispatcherFrame();
            owner.Dispatcher.BeginInvoke(
                DispatcherPriority.SystemIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            allowClose = true;
            owner.Close();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(completed);
    }

}
