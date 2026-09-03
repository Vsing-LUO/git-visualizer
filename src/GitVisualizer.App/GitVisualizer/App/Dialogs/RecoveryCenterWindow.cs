using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class RecoveryCenterWindow : Window, IComponentConnector
{
	public RecoveryPoint? SelectedPoint { get; private set; }

	public bool DeleteRequested { get; private set; }

	public RecoveryCenterWindow(IReadOnlyList<RecoveryPoint> points)
	{
		InitializeComponent();
		base.DataContext = points;
		RecoveryList.SelectedIndex = ((points.Count <= 0) ? (-1) : 0);
	}

	private void Restore_OnClick(object sender, RoutedEventArgs e)
	{
		if (!(RecoveryList.SelectedItem is RecoveryPoint selectedPoint))
		{
			MessageBox.Show(this, "请先选择一个恢复点。", "恢复中心");
			return;
		}
		SelectedPoint = selectedPoint;
		base.DialogResult = true;
	}

	private void Delete_OnClick(object sender, RoutedEventArgs e)
	{
		if (!(RecoveryList.SelectedItem is RecoveryPoint selectedPoint))
		{
			MessageBox.Show(this, "请先选择一个恢复点。", "恢复中心");
			return;
		}
		if (MessageBox.Show(this,
			"删除该恢复点及其 Git 安全引用？此操作无法撤销。",
			"删除恢复点", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		SelectedPoint = selectedPoint;
		DeleteRequested = true;
		base.DialogResult = true;
	}
}
