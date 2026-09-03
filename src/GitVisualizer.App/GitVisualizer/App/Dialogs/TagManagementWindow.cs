using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class TagManagementWindow : Window, IComponentConnector
{
	public TagManagementAction Action { get; private set; }

	public string TagName { get; private set; } = string.Empty;

	public TagManagementWindow(IReadOnlyList<TagInfo> tags)
	{
		InitializeComponent();
		base.DataContext = tags;
		ExistingTagsBox.SelectedIndex = ((tags.Count <= 0) ? (-1) : 0);
	}

	private void Create_OnClick(object sender, RoutedEventArgs e)
	{
		string text = TagNameBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
		{
			MessageBox.Show(this, "标签名不能为空，也不能包含空白字符。", "标签名无效");
			return;
		}
		Action = TagManagementAction.Create;
		TagName = text;
		base.DialogResult = true;
	}

	private void Delete_OnClick(object sender, RoutedEventArgs e)
	{
		if (!(ExistingTagsBox.SelectedItem is TagInfo tagInfo))
		{
			MessageBox.Show(this, "请先选择一个标签。", "标签管理");
		}
		else if (MessageBox.Show(this, "删除本地标签 " + tagInfo.Name + "？", "删除标签", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			Action = TagManagementAction.Delete;
			TagName = tagInfo.Name;
			base.DialogResult = true;
		}
	}
}
