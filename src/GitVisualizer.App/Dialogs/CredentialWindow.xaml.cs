using System.Windows;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CredentialWindow : Window
{
    public CredentialWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UserNameBox.Focus();
    }

    public RemoteCredential Credential => new(
        CredentialKind.HttpsToken,
        UserNameBox.Text.Trim(),
        TokenBox.Password,
        Remember: RememberBox.IsChecked == true);

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserNameBox.Text) || string.IsNullOrWhiteSpace(TokenBox.Password))
        {
            MessageBox.Show(this, "用户名和访问令牌不能为空。", "HTTPS 凭据");
            return;
        }
        DialogResult = true;
    }
}
