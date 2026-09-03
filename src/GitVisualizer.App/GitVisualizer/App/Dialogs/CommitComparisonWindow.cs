using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public sealed record CommitComparisonChoice(CommitNode Commit)
{
	public string ShortId => Commit.ShortId;

	public string Message => Commit.Message;

	public string DisplayText => $"{ShortId}  {Message}";
}

public partial class CommitComparisonWindow : Window, IComponentConnector
{
	public CommitNode? OldCommit => (OldCommitBox.SelectedItem as CommitComparisonChoice)?.Commit;

	public CommitNode? NewCommit => (NewCommitBox.SelectedItem as CommitComparisonChoice)?.Commit;

	public CommitComparisonWindow(IReadOnlyList<CommitNode> commits, string? preferredOldCommitId = null, string? preferredNewCommitId = null)
	{
		InitializeComponent();
		CommitComparisonChoice[] choices = commits.Select((CommitNode commit) => new CommitComparisonChoice(commit)).ToArray();
		base.DataContext = choices;
		NewCommitBox.SelectedItem = FindCommit(choices, preferredNewCommitId) ?? choices.FirstOrDefault();
		OldCommitBox.SelectedItem = FindCommit(choices, preferredOldCommitId) ?? choices.FirstOrDefault((CommitComparisonChoice choice) => !string.Equals(choice.Commit.Id, (NewCommitBox.SelectedItem as CommitComparisonChoice)?.Commit.Id, StringComparison.Ordinal)) ?? choices.FirstOrDefault();
	}

	private static CommitComparisonChoice? FindCommit(IReadOnlyList<CommitComparisonChoice> choices, string? id)
	{
		if (!string.IsNullOrWhiteSpace(id))
		{
			return choices.FirstOrDefault((CommitComparisonChoice choice) => string.Equals(choice.Commit.Id, id, StringComparison.Ordinal));
		}
		return null;
	}

	private void Swap_OnClick(object sender, RoutedEventArgs e)
	{
		object selectedItem = OldCommitBox.SelectedItem;
		OldCommitBox.SelectedItem = NewCommitBox.SelectedItem;
		NewCommitBox.SelectedItem = selectedItem;
	}

	private void Compare_OnClick(object sender, RoutedEventArgs e)
	{
		if ((object)OldCommit == null || (object)NewCommit == null)
		{
			MessageBox.Show(this, "请先选择旧提交和新提交。", "比较提交");
		}
		else
		{
			base.DialogResult = true;
		}
	}
}
