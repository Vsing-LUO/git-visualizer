using System.Windows;
using System.Windows.Controls;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CredentialWindow : Window
{
    private bool synchronizingToken;

    public CredentialWindow(
        string repositoryAddress,
        RemoteCredential? savedCredential = null)
    {
        InitializeComponent();
        RepositoryAddressBox.Text = repositoryAddress;
        if (savedCredential is { Kind: CredentialKind.HttpsToken })
        {
            UserNameBox.Text = savedCredential.UserName;
            SetToken(savedCredential.Secret);
            RememberBox.IsChecked = true;
            CredentialStatusText.Text = "已载入该仓库单独保存的凭据。";
            DeleteCredentialButton.Visibility = Visibility.Visible;
        }
        else
        {
            CredentialStatusText.Text =
                "该仓库尚未单独保存凭据；推送时将尝试使用系统 Git/GitHub 登录。";
            DeleteCredentialButton.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            UserNameBox.Focus();
            UserNameBox.SelectAll();
        };
    }

    public RemoteCredential Credential => new(
        CredentialKind.HttpsToken,
        UserNameBox.Text.Trim(),
        CurrentToken,
        Remember: RememberBox.IsChecked == true);

    public bool DeleteRequested { get; private set; }

    public string DisplayedRepositoryAddress => RepositoryAddressBox.Text;

    public string DisplayedUserName => UserNameBox.Text;

    public string DisplayedToken => CurrentToken;

    public bool IsTokenVisible => ShowTokenBox.IsChecked == true;

    private string CurrentToken => IsTokenVisible
        ? VisibleTokenBox.Text
        : TokenBox.Password;

    private void SetToken(string token)
    {
        synchronizingToken = true;
        TokenBox.Password = token;
        VisibleTokenBox.Text = token;
        synchronizingToken = false;
    }

    private void TokenBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!synchronizingToken)
        {
            synchronizingToken = true;
            VisibleTokenBox.Text = TokenBox.Password;
            synchronizingToken = false;
        }
    }

    private void VisibleTokenBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!synchronizingToken)
        {
            synchronizingToken = true;
            TokenBox.Password = VisibleTokenBox.Text;
            synchronizingToken = false;
        }
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

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserNameBox.Text) || string.IsNullOrWhiteSpace(CurrentToken))
        {
            MessageBox.Show(this, "用户名和访问令牌不能为空。", "HTTPS 凭据");
            return;
        }
        DialogResult = true;
    }

    private void DeleteCredential_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "只会删除当前仓库的已保存凭据，其他仓库不受影响。是否继续？",
            "删除仓库凭据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DeleteRequested = true;
        DialogResult = true;
    }
}
