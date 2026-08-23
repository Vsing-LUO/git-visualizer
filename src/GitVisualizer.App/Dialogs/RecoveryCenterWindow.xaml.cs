using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class RecoveryCenterWindow : Window
{
    public RecoveryCenterWindow(IReadOnlyList<RecoveryPoint> points)
    {
        InitializeComponent();
        DataContext = points;
        RecoveryList.SelectedIndex = points.Count > 0 ? 0 : -1;
    }

    public RecoveryPoint? SelectedPoint { get; private set; }

    private void Restore_OnClick(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not RecoveryPoint point)
        {
            MessageBox.Show(this, "请先选择一个恢复点。", "恢复中心");
            return;
        }
        SelectedPoint = point;
        DialogResult = true;
    }
}
