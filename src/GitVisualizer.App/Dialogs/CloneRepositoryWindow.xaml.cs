using System.Windows;
using System.Windows.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CloneRepositoryWindow : Window
{
    private bool synchronizingToken;

    public CloneRepositoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateAuthenticationFields();
            RepositoryUrlBox.Focus();
        };
    }

    public string RepositoryUrl => RepositoryUrlBox.Text.Trim();

    public bool UsesTokenLogin => TokenLoginOption.IsChecked == true;

    public RemoteCredential? Credential => UsesTokenLogin
        ? new RemoteCredential(
            CredentialKind.HttpsToken,
            UserNameBox.Text.Trim(),
            CurrentToken,
            Remember: RememberCredentialBox.IsChecked == true)
        : null;

    public string DisplayedToken => CurrentToken;

    private string CurrentToken => ShowTokenBox.IsChecked == true
        ? VisibleTokenBox.Text
        : TokenBox.Password;

    private void AuthenticationOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateAuthenticationFields();
        }
    }

    private void UpdateAuthenticationFields()
    {
        TokenFieldsPanel.Visibility = UsesTokenLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (UsesTokenLogin)
        {
            UserNameBox.Focus();
        }
    }

    private void TokenBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (synchronizingToken)
        {
            return;
        }
        synchronizingToken = true;
        VisibleTokenBox.Text = TokenBox.Password;
        synchronizingToken = false;
    }

    private void VisibleTokenBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (synchronizingToken)
        {
            return;
        }
        synchronizingToken = true;
        TokenBox.Password = VisibleTokenBox.Text;
        synchronizingToken = false;
    }

    private void ShowTokenBox_OnChecked(object sender, RoutedEventArgs e)
    {
        VisibleTokenBox.Text = TokenBox.Password;
        TokenBox.Visibility = Visibility.Collapsed;
        VisibleTokenBox.Visibility = Visibility.Visible;
        VisibleTokenBox.Focus();
        VisibleTokenBox.SelectAll();
    }

    private void ShowTokenBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        TokenBox.Password = VisibleTokenBox.Text;
        VisibleTokenBox.Visibility = Visibility.Collapsed;
        TokenBox.Visibility = Visibility.Visible;
        TokenBox.Focus();
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            MessageBox.Show(this, "请输入远程仓库地址。", "克隆远程仓库");
            return;
        }
        if (UsesTokenLogin &&
            (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            MessageBox.Show(
                this,
                "令牌登录仅适用于 HTTPS 仓库地址。请使用 https:// 开头的地址。",
                "克隆远程仓库");
            return;
        }
        if (UsesTokenLogin &&
            (string.IsNullOrWhiteSpace(UserNameBox.Text) ||
             string.IsNullOrWhiteSpace(CurrentToken)))
        {
            MessageBox.Show(this, "用户名和访问令牌不能为空。", "克隆远程仓库");
            return;
        }
        DialogResult = true;
    }
}
