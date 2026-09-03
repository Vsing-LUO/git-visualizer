using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;
using GitVisualizer.App.Services;

namespace GitVisualizer.App.Dialogs;

public partial class CredentialWindow : Window, IComponentConnector
{
	private bool synchronizingToken;

	public RemoteCredential Credential => new RemoteCredential(CredentialKind.HttpsToken, UserNameBox.Text.Trim(), CurrentToken, "", "", RememberBox.IsChecked == true);

	public bool DeleteRequested { get; private set; }

	public string DisplayedRepositoryAddress => RepositoryAddressBox.Text;

	public string DisplayedUserName => UserNameBox.Text;

	public string DisplayedToken => CurrentToken;

	public bool IsTokenVisible => ShowTokenBox.IsChecked == true;

	private string CurrentToken
	{
		get
		{
			if (!IsTokenVisible)
			{
				return TokenBox.Password;
			}
			return VisibleTokenBox.Text;
		}
	}

	public CredentialWindow(string repositoryAddress, RemoteCredential? savedCredential = null)
	{
		InitializeComponent();
		RepositoryAddressBox.Text = repositoryAddress;
		if (!RemoteUrlSecurity.IsHttps(repositoryAddress))
		{
			CredentialStatusText.Text = "访问令牌只能用于 HTTPS 远程仓库；当前地址不会加载或发送令牌。";
			SaveCredentialButton.IsEnabled = false;
			DeleteCredentialButton.Visibility = Visibility.Visible;
		}
		else if ((object)savedCredential != null && savedCredential.Kind == CredentialKind.HttpsToken)
		{
			UserNameBox.Text = savedCredential.UserName;
			SetToken(savedCredential.Secret);
			RememberBox.IsChecked = true;
			CredentialStatusText.Text = "已载入该仓库单独保存的凭据。";
			DeleteCredentialButton.Visibility = Visibility.Visible;
		}
		else
		{
			CredentialStatusText.Text = "该仓库尚未单独保存凭据；推送时将尝试使用系统 Git/GitHub 登录。";
			DeleteCredentialButton.Visibility = Visibility.Collapsed;
		}
		base.Loaded += delegate
		{
			UserNameBox.Focus();
			UserNameBox.SelectAll();
		};
	}

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
		if (!RemoteUrlSecurity.IsHttps(DisplayedRepositoryAddress))
		{
			MessageBox.Show(this, "访问令牌只能保存并发送到 HTTPS 远程仓库。", "HTTPS 凭据");
		}
		else if (string.IsNullOrWhiteSpace(UserNameBox.Text) || string.IsNullOrWhiteSpace(CurrentToken))
		{
			MessageBox.Show(this, "用户名和访问令牌不能为空。", "HTTPS 凭据");
		}
		else
		{
			base.DialogResult = true;
		}
	}

	private void DeleteCredential_OnClick(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(this, "只会删除当前仓库的已保存凭据，其他仓库不受影响。是否继续？", "删除仓库凭据", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			DeleteRequested = true;
			base.DialogResult = true;
		}
	}
}
