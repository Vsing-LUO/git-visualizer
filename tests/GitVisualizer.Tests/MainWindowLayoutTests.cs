using GitVisualizer.App;

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
}
