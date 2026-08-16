using System.Windows;
using System.Windows.Controls;

namespace GitVisualizer.App.Dialogs;

public partial class ForcePushConfirmationWindow : Window
{
    private readonly string branchName;

    public ForcePushConfirmationWindow(string remoteName, string branchName)
    {
        this.branchName = branchName;
        InitializeComponent();
        DataContext = $"目标：{branchName} → {remoteName}/{branchName}";
        InstructionText.Text = $"请输入分支名 “{branchName}” 以确认：";
    }

    private void Confirmation_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton is not null)
        {
            ConfirmButton.IsEnabled = string.Equals(
                ConfirmationBox.Text.Trim(), branchName, StringComparison.Ordinal);
        }
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
