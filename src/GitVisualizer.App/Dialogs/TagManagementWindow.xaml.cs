using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class TagManagementWindow : Window
{
    public TagManagementWindow(IReadOnlyList<TagInfo> tags)
    {
        InitializeComponent();
        DataContext = tags;
        ExistingTagsBox.SelectedIndex = tags.Count > 0 ? 0 : -1;
    }

    public TagManagementAction Action { get; private set; }
    public string TagName { get; private set; } = string.Empty;

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        var name = TagNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
        {
            MessageBox.Show(this, "标签名不能为空，也不能包含空白字符。", "标签名无效");
            return;
        }
        Action = TagManagementAction.Create;
        TagName = name;
        DialogResult = true;
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (ExistingTagsBox.SelectedItem is not TagInfo tag)
        {
            MessageBox.Show(this, "请先选择一个标签。", "标签管理");
            return;
        }
        if (MessageBox.Show(this, $"删除本地标签 {tag.Name}？", "删除标签",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        Action = TagManagementAction.Delete;
        TagName = tag.Name;
        DialogResult = true;
    }
}

public enum TagManagementAction { None, Create, Delete }
