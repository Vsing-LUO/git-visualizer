using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CommitComparisonWindow : Window
{
    public CommitComparisonWindow(
        IReadOnlyList<CommitNode> commits,
        string? preferredOldCommitId = null,
        string? preferredNewCommitId = null)
    {
        InitializeComponent();
        DataContext = commits;

        NewCommitBox.SelectedItem = FindCommit(commits, preferredNewCommitId) ?? commits.FirstOrDefault();
        OldCommitBox.SelectedItem = FindCommit(commits, preferredOldCommitId) ??
                                    commits.FirstOrDefault(commit =>
                                        !string.Equals(
                                            commit.Id,
                                            (NewCommitBox.SelectedItem as CommitNode)?.Id,
                                            StringComparison.Ordinal)) ??
                                    commits.FirstOrDefault();
    }

    public CommitNode? OldCommit => OldCommitBox.SelectedItem as CommitNode;
    public CommitNode? NewCommit => NewCommitBox.SelectedItem as CommitNode;

    private static CommitNode? FindCommit(IReadOnlyList<CommitNode> commits, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : commits.FirstOrDefault(commit =>
                string.Equals(commit.Id, id, StringComparison.Ordinal));

    private void Swap_OnClick(object sender, RoutedEventArgs e)
    {
        var oldCommit = OldCommitBox.SelectedItem;
        OldCommitBox.SelectedItem = NewCommitBox.SelectedItem;
        NewCommitBox.SelectedItem = oldCommit;
    }

    private void Compare_OnClick(object sender, RoutedEventArgs e)
    {
        if (OldCommit is null || NewCommit is null)
        {
            MessageBox.Show(this, "请先选择旧提交和新提交。", "比较提交");
            return;
        }
        DialogResult = true;
    }
}
