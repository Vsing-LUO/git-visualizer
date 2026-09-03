using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CreateTagWindow : Window, IComponentConnector
{
	private sealed record TagTargetItem(CommitNode Commit)
	{
		public string ShortId => Commit.ShortId;

		public string Message => Commit.Message;

		public string DisplayText => $"{ShortId} - {Message}";
	}

	private readonly HashSet<string> existingTagNames;

	public string TagName => TagNameBox.Text.Trim();

	public string TargetCommitId => (TargetCommitBox.SelectedItem as TagTargetItem)?.Commit.Id ?? string.Empty;

	public GitTagType TagType => AnnotatedOption.IsChecked == true
		? GitTagType.Annotated
		: GitTagType.Lightweight;

	public string? TagMessage => TagType == GitTagType.Annotated
		? TagMessageBox.Text.Trim()
		: null;

	public CreateTagWindow(
		IReadOnlyList<CommitNode> commits,
		CommitNode selectedCommit,
		IReadOnlyList<TagInfo> tags)
	{
		existingTagNames = tags.Select(tag => tag.Name).ToHashSet(System.StringComparer.Ordinal);
		InitializeComponent();
		TagTargetItem[] targets = commits
			.Select(commit => new TagTargetItem(commit))
			.ToArray();
		TargetCommitBox.ItemsSource = targets;
		TargetCommitBox.SelectedItem = targets.FirstOrDefault(item => item.Commit.Id == selectedCommit.Id)
			?? new TagTargetItem(selectedCommit);
		Loaded += (_, _) =>
		{
			TagNameBox.Focus();
			UpdateState();
		};
	}

	private void TagType_OnChanged(object sender, RoutedEventArgs e)
	{
		if (AnnotationPanel == null)
		{
			return;
		}

		bool annotated = AnnotatedOption.IsChecked == true;
		AnnotationPanel.Visibility = annotated ? Visibility.Visible : Visibility.Collapsed;
		TagTypeHint.Text = annotated
			? "附注 Tag 会在推送对应分支时自动上传，适合正式版本发布。"
			: "轻量 Tag 只保存在本地，普通推送不会上传。";
		UpdateState();
	}

	private void Input_OnChanged(object sender, RoutedEventArgs e)
	{
		UpdateState();
	}

	private void UpdateState()
	{
		if (CreateButton == null || ValidationText == null)
		{
			return;
		}

		string error = ValidateInput();
		ValidationText.Text = error;
		CreateButton.IsEnabled = string.IsNullOrEmpty(error);
	}

	private string ValidateInput()
	{
		if (TagNameBox == null || TargetCommitBox == null || AnnotatedOption == null || TagMessageBox == null)
		{
			return string.Empty;
		}

		string name = TagNameBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(name))
		{
			return "请输入 Tag 名称。";
		}
		if (name.Any(char.IsWhiteSpace))
		{
			return "Tag 名称不能包含空白字符。";
		}
		if (existingTagNames.Contains(name))
		{
			return $"Tag“{name}”已经存在，请使用其他名称。";
		}
		if (TargetCommitBox.SelectedItem is not TagTargetItem)
		{
			return "请选择目标提交。";
		}
		if (AnnotatedOption.IsChecked == true && string.IsNullOrWhiteSpace(TagMessageBox.Text))
		{
			return "附注 Tag 需要填写 Tag 说明。";
		}
		return string.Empty;
	}

	private void Create_OnClick(object sender, RoutedEventArgs e)
	{
		string error = ValidateInput();
		if (!string.IsNullOrEmpty(error))
		{
			ValidationText.Text = error;
			return;
		}

		DialogResult = true;
	}
}
