using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class StashManagementWindow : Window, IComponentConnector
{
	public StashManagementAction Action { get; private set; }

	public int SelectedIndex { get; private set; } = -1;

	public string StashMessage => MessageBox.Text.Trim();

	public StashManagementWindow(IReadOnlyList<StashInfo> stashes)
	{
		InitializeComponent();
		base.DataContext = stashes;
		StashList.SelectedIndex = ((stashes.Count <= 0) ? (-1) : 0);
	}

	private void Save_OnClick(object sender, RoutedEventArgs e)
	{
		Action = StashManagementAction.Save;
		base.DialogResult = true;
	}

	private void Apply_OnClick(object sender, RoutedEventArgs e)
	{
		Choose(StashManagementAction.Apply);
	}

	private void Pop_OnClick(object sender, RoutedEventArgs e)
	{
		Choose(StashManagementAction.Pop);
	}

	private void Delete_OnClick(object sender, RoutedEventArgs e)
	{
		if (!(StashList.SelectedItem is StashInfo stashInfo))
		{
			ThemedMessageBox.Show(this, "请先选择一个临时现场。", "临时现场");
		}
		else if (ThemedMessageBox.Show(this, $"删除 stash@{{{stashInfo.Index}}}？安全引用仍会保留。", "删除临时现场", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			SelectedIndex = stashInfo.Index;
			Action = StashManagementAction.Delete;
			base.DialogResult = true;
		}
	}

	private void Choose(StashManagementAction action)
	{
		if (!(StashList.SelectedItem is StashInfo stashInfo))
		{
			ThemedMessageBox.Show(this, "请先选择一个临时现场。", "临时现场");
			return;
		}
		SelectedIndex = stashInfo.Index;
		Action = action;
		base.DialogResult = true;
	}
}
