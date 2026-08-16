using System.Windows;

namespace GitVisualizer.App.Dialogs;

public partial class RebaseWindow : Window
{
    public RebaseWindow(IReadOnlyList<string> branches)
    {
        InitializeComponent();
        DataContext = branches;
        UpstreamBox.SelectedIndex = branches.Count > 0 ? 0 : -1;
    }

    public string UpstreamBranch => UpstreamBox.Text.Trim();
    public string? OntoBranch => string.IsNullOrWhiteSpace(OntoBox.Text) ? null : OntoBox.Text.Trim();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UpstreamBranch))
        {
            MessageBox.Show(this, "请选择上游分支。", "变基");
            return;
        }
        if (ConfirmRiskBox.IsChecked != true)
        {
            MessageBox.Show(this, "请先确认你理解变基会重写提交历史。", "变基");
            return;
        }
        DialogResult = true;
    }
}
