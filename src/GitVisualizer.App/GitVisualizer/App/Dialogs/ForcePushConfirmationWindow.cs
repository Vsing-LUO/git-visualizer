using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace GitVisualizer.App.Dialogs;

public partial class ForcePushConfirmationWindow : Window, IComponentConnector
{
	private readonly string branchName;

	public ForcePushConfirmationWindow(string remoteName, string branchName)
	{
		this.branchName = branchName;
		InitializeComponent();
		base.DataContext = $"目标：{branchName} → {remoteName}/{branchName}";
		InstructionText.Text = "请输入分支名 “" + branchName + "” 以确认：";
	}

	private void Confirmation_OnTextChanged(object sender, TextChangedEventArgs e)
	{
		if (ConfirmButton != null)
		{
			ConfirmButton.IsEnabled = string.Equals(ConfirmationBox.Text.Trim(), branchName, StringComparison.Ordinal);
		}
	}

	private void Confirm_OnClick(object sender, RoutedEventArgs e)
	{
		base.DialogResult = true;
	}
}
