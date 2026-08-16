using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class ResetModeWindowTests
{
    [Fact]
    public void ChineseOptions_DetailToggleAndHardConfirmation_AreWired()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ResetModeWindow? window = null;
            try
            {
                window = new ResetModeWindow("HEAD → main", "abc12345", "目标提交");
                window.Show();

                var detailToggle = Assert.IsType<ToggleButton>(
                    window.FindName("DetailedExplanationToggle"));
                var mixedDetail = Assert.IsType<TextBlock>(
                    window.FindName("MixedDetailedText"));
                var softOption = Assert.IsType<RadioButton>(
                    window.FindName("SoftOption"));
                var hardOption = Assert.IsType<RadioButton>(
                    window.FindName("HardOption"));
                var hardConfirmation = Assert.IsType<CheckBox>(
                    window.FindName("HardConfirmation"));
                var confirmButton = Assert.IsType<Button>(
                    window.FindName("ConfirmButton"));

                Assert.Equal(GitResetMode.Mixed, window.SelectedMode);
                Assert.True(confirmButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, mixedDetail.Visibility);

                detailToggle.IsChecked = true;
                Assert.True(window.IsDetailedExplanation);
                Assert.Equal(Visibility.Visible, mixedDetail.Visibility);

                softOption.IsChecked = true;
                Assert.Equal(GitResetMode.Soft, window.SelectedMode);
                Assert.True(confirmButton.IsEnabled);

                hardOption.IsChecked = true;
                Assert.Equal(GitResetMode.Hard, window.SelectedMode);
                Assert.Equal(Visibility.Visible, hardConfirmation.Visibility);
                Assert.False(confirmButton.IsEnabled);

                hardConfirmation.IsChecked = true;
                Assert.True(confirmButton.IsEnabled);
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
