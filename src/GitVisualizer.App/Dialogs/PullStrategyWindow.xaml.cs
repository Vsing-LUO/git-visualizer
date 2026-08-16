using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class PullStrategyWindow : Window
{
    public PullStrategyWindow(PullStrategy initialStrategy = PullStrategy.Merge)
    {
        InitializeComponent();
        switch (initialStrategy)
        {
            case PullStrategy.Rebase:
                RebaseOption.IsChecked = true;
                break;
            case PullStrategy.FastForwardOnly:
                FastForwardOnlyOption.IsChecked = true;
                break;
            default:
                MergeOption.IsChecked = true;
                break;
        }
    }

    public PullStrategy SelectedStrategy =>
        RebaseOption.IsChecked == true
            ? PullStrategy.Rebase
            : FastForwardOnlyOption.IsChecked == true
                ? PullStrategy.FastForwardOnly
                : PullStrategy.Merge;

    private void Confirm_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
