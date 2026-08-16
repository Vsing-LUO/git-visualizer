using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class RemoteConfigurationWindow : Window
{
    private static readonly Regex ValidRemoteName =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    public RemoteConfigurationWindow(IEnumerable<RemoteInfo> remotes)
    {
        InitializeComponent();
        ConfiguredRemotes = new ObservableCollection<RemoteInfo>(remotes);
        DataContext = this;
        NoRemotesHint.Visibility =
            ConfiguredRemotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
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

    public ObservableCollection<RemoteInfo> ConfiguredRemotes { get; }

    public string? OriginalName { get; private set; }

    public string RemoteName => RemoteNameBox.Text.Trim();

    public string RemoteUrl => RemoteUrlBox.Text.Trim();

    public string? RemoteToRemove { get; private set; }

    private void ConfiguredRemotesList_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ConfiguredRemotesList.SelectedItem is not RemoteInfo remote)
        {
            DeleteRemoteButton.IsEnabled = false;
            return;
        }

        DeleteRemoteButton.IsEnabled = true;
        OriginalName = remote.Name;
        RemoteNameBox.Text = remote.Name;
        RemoteUrlBox.Text = remote.FetchUrl;
        RemoteUrlBox.Focus();
        RemoteUrlBox.SelectAll();
    }

    private void NewRemote_OnClick(object sender, RoutedEventArgs e) =>
        BeginNewRemote();

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
        if (ConfiguredRemotesList.SelectedItem is not RemoteInfo remote)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定删除远程仓库“{remote.Name}”吗？\n\n{remote.FetchUrl}\n\n" +
            "这只会删除当前本地仓库中的远程配置，不会删除服务器上的仓库。",
            "删除远程仓库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        RemoteToRemove = remote.Name;
        DialogResult = true;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ValidRemoteName.IsMatch(RemoteName))
        {
            MessageBox.Show(
                this,
                "远程名称只能包含字母、数字、点、下划线或连字符，并且必须以字母或数字开头。",
                "远程名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RemoteNameBox.Focus();
            return;
        }
        if (!GitRemoteAddress.TryNormalize(RemoteUrl, out var normalizedUrl))
        {
            MessageBox.Show(
                this,
                "请输入有效的远程仓库地址。",
                "仓库地址无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RemoteUrlBox.Focus();
            return;
        }
        RemoteUrlBox.Text = normalizedUrl;
        if (OriginalName is null &&
            ConfiguredRemotes.Any(remote =>
                string.Equals(
                    remote.Name,
                    RemoteName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                $"远程名称 {RemoteName} 已存在。请选择该远程进行更新，或使用其他名称。",
                "远程名称已存在",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RemoteNameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
