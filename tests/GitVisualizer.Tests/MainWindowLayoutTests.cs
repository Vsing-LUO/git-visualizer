using GitVisualizer.App;
using GitVisualizer.App.Controls;

namespace GitVisualizer.Tests;

public sealed class MainWindowLayoutTests
{
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
    [InlineData(100, 280, 100)]
    [InlineData(280, 280, 280)]
    [InlineData(350, 280, 280)]
    [InlineData(500, 280, 280)]
    [InlineData(240, 280, 240)]
    [InlineData(100, 0, 0)]
    [InlineData(-10, 280, 0)]
    public void DiffFollowerStopsWhenItsLongestLineIsFullyVisible(
        double driverOffset,
        double followerMaximum,
        double expected)
    {
        Assert.Equal(
            expected,
            DiffBlockComparisonControl.CalculateFollowerOffset(
                driverOffset,
                followerMaximum));
    }

    [Theory]
    [InlineData(0, 248, 0)]
    [InlineData(240, 248, 0)]
    [InlineData(241, 248, 1)]
    [InlineData(400, 248, 160)]
    public void DiffScrollMaximumIncludesTrailingVisibilityPadding(
        double contentWidth,
        double viewportWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            DiffBlockComparisonControl.CalculateMaximumOffset(
                contentWidth,
                viewportWidth));
    }
}
