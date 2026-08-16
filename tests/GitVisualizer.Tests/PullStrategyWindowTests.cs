using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;
using System.Windows.Controls;

namespace GitVisualizer.Tests;

public sealed class PullStrategyWindowTests
{
    [Fact]
    public void InitialAndChangedSelections_MapToPullStrategies()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            PullStrategyWindow? window = null;
            try
            {
                window = new PullStrategyWindow(PullStrategy.Rebase);
                window.Show();

                Assert.Equal(PullStrategy.Rebase, window.SelectedStrategy);
                Assert.IsType<RadioButton>(
                    window.FindName("FastForwardOnlyOption")).IsChecked = true;
                Assert.Equal(PullStrategy.FastForwardOnly, window.SelectedStrategy);
                Assert.IsType<RadioButton>(
                    window.FindName("MergeOption")).IsChecked = true;
                Assert.Equal(PullStrategy.Merge, window.SelectedStrategy);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
