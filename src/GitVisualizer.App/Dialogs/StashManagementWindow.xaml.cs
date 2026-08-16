using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class StashManagementWindow : Window
{
    public StashManagementWindow(IReadOnlyList<StashInfo> stashes)
    {
        InitializeComponent();
        DataContext = stashes;
        StashList.SelectedIndex = stashes.Count > 0 ? 0 : -1;
    }

    public StashManagementAction Action { get; private set; }
    public int SelectedIndex { get; private set; } = -1;
    public string StashMessage => MessageBox.Text.Trim();

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Action = StashManagementAction.Save;
        DialogResult = true;
    }

    private void Apply_OnClick(object sender, RoutedEventArgs e) => Choose(StashManagementAction.Apply);
    private void Pop_OnClick(object sender, RoutedEventArgs e) => Choose(StashManagementAction.Pop);

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (StashList.SelectedItem is not StashInfo stash)
        {
            System.Windows.MessageBox.Show(this, "请先选择一个临时现场。", "临时现场");
            return;
        }
        if (System.Windows.MessageBox.Show(this, $"删除 stash@{{{stash.Index}}}？安全引用仍会保留。", "删除临时现场",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        SelectedIndex = stash.Index;
        Action = StashManagementAction.Delete;
        DialogResult = true;
    }

    private void Choose(StashManagementAction action)
    {
        if (StashList.SelectedItem is not StashInfo stash)
        {
            System.Windows.MessageBox.Show(this, "请先选择一个临时现场。", "临时现场");
            return;
        }
        SelectedIndex = stash.Index;
        Action = action;
        DialogResult = true;
    }
}

public enum StashManagementAction { None, Save, Apply, Pop, Delete }
