using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class PullStrategyWindow : Window, IComponentConnector
{
	private readonly IReadOnlyList<BranchInfo> branches;

	public PullStrategy SelectedStrategy
	{
		get
		{
			if (RebaseOption.IsChecked != true)
			{
				if (FastForwardOnlyOption.IsChecked != true)
				{
					return PullStrategy.Merge;
				}
				return PullStrategy.FastForwardOnly;
			}
			return PullStrategy.Rebase;
		}
	}

	public RemoteInfo? SelectedRemote => RemoteBox.SelectedItem as RemoteInfo;

	public string SelectedRemoteBranch => BranchBox.Text.Trim();

	public PullStrategyWindow(PullStrategy initialStrategy = PullStrategy.Merge)
		: this(Array.Empty<RemoteInfo>(), Array.Empty<BranchInfo>(), null, initialStrategy)
	{
	}

	public PullStrategyWindow(IReadOnlyList<RemoteInfo> remotes, IReadOnlyList<BranchInfo> branches, RemoteInfo? selectedRemote, PullStrategy initialStrategy = PullStrategy.Merge)
	{
		this.branches = branches;
		InitializeComponent();
		RemoteBox.ItemsSource = remotes;
		RemoteBox.SelectedItem = remotes.FirstOrDefault((RemoteInfo remote) => remote.Name.Equals(selectedRemote?.Name, StringComparison.OrdinalIgnoreCase)) ?? remotes.FirstOrDefault();
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

	private void Remote_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!(RemoteBox.SelectedItem is RemoteInfo remoteInfo))
		{
			BranchBox.ItemsSource = null;
			return;
		}
		string prefix = remoteInfo.Name + "/";
		string[] array = (from branch in branches
			where branch.IsRemote && branch.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			select branch.FriendlyName.Substring(prefix.Length) into name
			where !name.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
			select name).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string name) => name, StringComparer.OrdinalIgnoreCase).ToArray();
		BranchBox.ItemsSource = array;
		BranchBox.SelectedItem = array.FirstOrDefault();
	}

	private void Confirm_OnClick(object sender, RoutedEventArgs e)
	{
		if (RemoteBox.Items.Count > 0 && ((object)SelectedRemote == null || string.IsNullOrWhiteSpace(SelectedRemoteBranch)))
		{
			MessageBox.Show(this, "请选择远程仓库和远程分支。", "拉取");
		}
		else
		{
			base.DialogResult = true;
		}
	}
}
