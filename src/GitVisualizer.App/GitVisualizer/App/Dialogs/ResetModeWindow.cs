using System.Windows;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class ResetModeWindow : Window, IComponentConnector
{
	public GitResetMode SelectedMode
	{
		get
		{
			if (SoftOption.IsChecked != true)
			{
				if (HardOption.IsChecked != true)
				{
					return GitResetMode.Mixed;
				}
				return GitResetMode.Hard;
			}
			return GitResetMode.Soft;
		}
	}

	public bool IsDetailedExplanation => DetailedExplanationToggle.IsChecked == true;

	public ResetModeWindow(string branchName, string commitShortId, string commitMessage)
	{
		InitializeComponent();
		TargetText.Text = $"当前分支：{branchName}  ·  目标版本：{commitShortId} {commitMessage}";
		MixedOption.IsChecked = true;
		UpdateSelectionState();
	}

	private void DetailedExplanationToggle_OnChanged(object sender, RoutedEventArgs e)
	{
		Visibility visibility = ((!IsDetailedExplanation) ? Visibility.Collapsed : Visibility.Visible);
		GeneralDetailedText.Visibility = visibility;
		MixedDetailedText.Visibility = visibility;
		SoftDetailedText.Visibility = visibility;
		HardDetailedText.Visibility = visibility;
		DetailedExplanationToggle.Content = (IsDetailedExplanation ? "显示简短解释" : "显示详细解释");
	}

	private void ModeOption_OnChecked(object sender, RoutedEventArgs e)
	{
		UpdateSelectionState();
	}

	private void HardConfirmation_OnChanged(object sender, RoutedEventArgs e)
	{
		UpdateSelectionState();
	}

	private void UpdateSelectionState()
	{
		if (ConfirmButton != null && ResultSummaryText != null && HardConfirmation != null)
		{
			switch (SelectedMode)
			{
			case GitResetMode.Soft:
				ResultSummaryText.Text = "结果：后续提交从当前分支移除；文件修改保留，并继续处于已暂存状态。";
				ConfirmButton.Content = "回退并保留暂存修改";
				HardConfirmation.Visibility = Visibility.Collapsed;
				HardConfirmation.IsChecked = false;
				ConfirmButton.IsEnabled = true;
				break;
			case GitResetMode.Hard:
				ResultSummaryText.Text = "结果：彻底恢复到所选版本；后续提交和当前受 Git 管理的文件修改将被丢弃。";
				ConfirmButton.Content = "彻底回到所选版本";
				HardConfirmation.Visibility = Visibility.Visible;
				ConfirmButton.IsEnabled = HardConfirmation.IsChecked == true;
				break;
			default:
				ResultSummaryText.Text = "结果：后续提交从当前分支移除；文件修改保留，并显示为未暂存。";
				ConfirmButton.Content = "回退并保留未暂存修改";
				HardConfirmation.Visibility = Visibility.Collapsed;
				HardConfirmation.IsChecked = false;
				ConfirmButton.IsEnabled = true;
				break;
			}
		}
	}

	private void Confirm_OnClick(object sender, RoutedEventArgs e)
	{
		if (SelectedMode != GitResetMode.Hard || HardConfirmation.IsChecked == true)
		{
			base.DialogResult = true;
		}
	}
}
