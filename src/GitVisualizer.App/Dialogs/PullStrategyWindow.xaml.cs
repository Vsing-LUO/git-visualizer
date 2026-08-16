using System.Windows;
using System.Windows.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class PullStrategyWindow : Window
{
    private readonly IReadOnlyList<BranchInfo> branches;

    public PullStrategyWindow(PullStrategy initialStrategy = PullStrategy.Merge)
        : this([], [], null, initialStrategy)
    {
    }

    public PullStrategyWindow(
        IReadOnlyList<RemoteInfo> remotes,
        IReadOnlyList<BranchInfo> branches,
        RemoteInfo? selectedRemote,
        PullStrategy initialStrategy = PullStrategy.Merge)
    {
        this.branches = branches;
        InitializeComponent();
        RemoteBox.ItemsSource = remotes;
        RemoteBox.SelectedItem = remotes.FirstOrDefault(remote =>
                                     remote.Name.Equals(selectedRemote?.Name, StringComparison.OrdinalIgnoreCase))
                                 ?? remotes.FirstOrDefault();
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

    public RemoteInfo? SelectedRemote => RemoteBox.SelectedItem as RemoteInfo;

    public string SelectedRemoteBranch => BranchBox.Text.Trim();

    private void Remote_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteBox.SelectedItem is not RemoteInfo remote)
        {
            BranchBox.ItemsSource = null;
            return;
        }
        var prefix = remote.Name + "/";
        var remoteBranches = branches
            .Where(branch => branch.IsRemote &&
                             branch.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(branch => branch.FriendlyName[prefix.Length..])
            .Where(name => !name.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        BranchBox.ItemsSource = remoteBranches;
        BranchBox.SelectedItem = remoteBranches.FirstOrDefault();
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (RemoteBox.Items.Count > 0 &&
            (SelectedRemote is null || string.IsNullOrWhiteSpace(SelectedRemoteBranch)))
        {
            MessageBox.Show(this, "请选择远程仓库和远程分支。", "拉取");
            return;
        }
        DialogResult = true;
    }
}
