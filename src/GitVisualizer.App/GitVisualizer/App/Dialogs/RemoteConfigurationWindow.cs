using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class RemoteConfigurationWindow : Window, IComponentConnector
{
	private static readonly Regex ValidRemoteName = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

	public ObservableCollection<RemoteInfo> ConfiguredRemotes { get; }

	public string? OriginalName { get; private set; }

	public string RemoteName => RemoteNameBox.Text.Trim();

	public string RemoteUrl => RemoteUrlBox.Text.Trim();

	public string? RemoteToRemove { get; private set; }

	public RemoteConfigurationWindow(IEnumerable<RemoteInfo> remotes)
	{
		InitializeComponent();
		ConfiguredRemotes = new ObservableCollection<RemoteInfo>(remotes);
		base.DataContext = this;
		NoRemotesHint.Visibility = ((ConfiguredRemotes.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		base.Loaded += delegate
		{
			if (ConfiguredRemotes.Count > 0)
			{
				ConfiguredRemotesList.SelectedIndex = 0;
			}
			else
			{
				BeginNewRemote();
			}
		};
	}

	private void ConfiguredRemotesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!(ConfiguredRemotesList.SelectedItem is RemoteInfo remoteInfo))
		{
			DeleteRemoteButton.IsEnabled = false;
			return;
		}
		DeleteRemoteButton.IsEnabled = true;
		OriginalName = remoteInfo.Name;
		RemoteNameBox.Text = remoteInfo.Name;
		RemoteUrlBox.Text = remoteInfo.FetchUrl;
		RemoteUrlBox.Focus();
		RemoteUrlBox.SelectAll();
	}

	private void NewRemote_OnClick(object sender, RoutedEventArgs e)
	{
		BeginNewRemote();
	}

	private void BeginNewRemote()
	{
		ConfiguredRemotesList.SelectedItem = null;
		DeleteRemoteButton.IsEnabled = false;
		OriginalName = null;
		RemoteNameBox.Text = "origin";
		RemoteUrlBox.Text = string.Empty;
		RemoteUrlBox.Focus();
	}

	private void DeleteRemote_OnClick(object sender, RoutedEventArgs e)
	{
		if (ConfiguredRemotesList.SelectedItem is RemoteInfo remoteInfo && MessageBox.Show(this, $"确定删除远程仓库“{remoteInfo.Name}”吗？\n\n{remoteInfo.FetchUrl}\n\n" + "这只会删除当前本地仓库中的远程配置，不会删除服务器上的仓库。", "删除远程仓库", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			RemoteToRemove = remoteInfo.Name;
			base.DialogResult = true;
		}
	}

	private void Save_OnClick(object sender, RoutedEventArgs e)
	{
		if (!ValidRemoteName.IsMatch(RemoteName))
		{
			MessageBox.Show(this, "远程名称只能包含字母、数字、点、下划线或连字符，并且必须以字母或数字开头。", "远程名称无效", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			RemoteNameBox.Focus();
			return;
		}
		if (!GitRemoteAddress.TryNormalize(RemoteUrl, out string normalized))
		{
			MessageBox.Show(this, "请输入有效的远程仓库地址。HTTP/HTTPS 地址不得内嵌用户名、密码或访问令牌。", "仓库地址无效", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			RemoteUrlBox.Focus();
			return;
		}
		RemoteUrlBox.Text = normalized;
		if (OriginalName == null && ConfiguredRemotes.Any((RemoteInfo remote) => string.Equals(remote.Name, RemoteName, StringComparison.OrdinalIgnoreCase)))
		{
			MessageBox.Show(this, "远程名称 " + RemoteName + " 已存在。请选择该远程进行更新，或使用其他名称。", "远程名称已存在", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			RemoteNameBox.Focus();
		}
		else
		{
			base.DialogResult = true;
		}
	}
}
