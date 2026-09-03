using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;

namespace GitVisualizer.App.Dialogs;

public partial class RebaseWindow : Window, IComponentConnector
{
	public string UpstreamBranch => UpstreamBox.Text.Trim();

	public string? OntoBranch
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(OntoBox.Text))
			{
				return OntoBox.Text.Trim();
			}
			return null;
		}
	}

	public RebaseWindow(IReadOnlyList<string> branches)
	{
		InitializeComponent();
		base.DataContext = branches;
		UpstreamBox.SelectedIndex = ((branches.Count <= 0) ? (-1) : 0);
	}

	private void Confirm_OnClick(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(UpstreamBranch))
		{
			MessageBox.Show(this, "请选择上游分支。", "变基");
		}
		else if (ConfirmRiskBox.IsChecked != true)
		{
			MessageBox.Show(this, "请先确认你理解变基会重写提交历史。", "变基");
		}
		else
		{
			base.DialogResult = true;
		}
	}
}
