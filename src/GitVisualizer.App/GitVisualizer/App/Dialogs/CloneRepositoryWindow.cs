using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class CloneRepositoryWindow : Window, IComponentConnector
{
	private bool synchronizingToken;

	public string RepositoryUrl => RepositoryUrlBox.Text.Trim();

	public bool UsesTokenLogin => TokenLoginOption.IsChecked == true;

	public RemoteCredential? Credential
	{
		get
		{
			if (!UsesTokenLogin)
			{
				return null;
			}
			return new RemoteCredential(CredentialKind.HttpsToken, UserNameBox.Text.Trim(), CurrentToken, "", "", RememberCredentialBox.IsChecked == true);
		}
	}

	public string DisplayedToken => CurrentToken;

	private string CurrentToken
	{
		get
		{
			if (ShowTokenBox.IsChecked != true)
			{
				return TokenBox.Password;
			}
			return VisibleTokenBox.Text;
		}
	}

	public CloneRepositoryWindow()
	{
		InitializeComponent();
		base.Loaded += delegate
		{
			UpdateAuthenticationFields();
			RepositoryUrlBox.Focus();
		};
	}

	private void AuthenticationOption_OnChecked(object sender, RoutedEventArgs e)
	{
		if (base.IsLoaded)
		{
			UpdateAuthenticationFields();
		}
	}

	private void UpdateAuthenticationFields()
	{
		TokenFieldsPanel.Visibility = ((!UsesTokenLogin) ? Visibility.Collapsed : Visibility.Visible);
		if (UsesTokenLogin)
		{
			UserNameBox.Focus();
		}
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

	private void Continue_OnClick(object sender, RoutedEventArgs e)
	{
		if (!TryValidateRepositoryUrl(RepositoryUrl, UsesTokenLogin, out string normalized, out string error))
		{
			MessageBox.Show(this, error, "克隆远程仓库");
			RepositoryUrlBox.Focus();
		}
		else if (UsesTokenLogin && (string.IsNullOrWhiteSpace(UserNameBox.Text) || string.IsNullOrWhiteSpace(CurrentToken)))
		{
			MessageBox.Show(this, "用户名和访问令牌不能为空。", "克隆远程仓库");
		}
		else
		{
			RepositoryUrlBox.Text = normalized;
			base.DialogResult = true;
		}
	}

	internal static bool TryValidateRepositoryUrl(
		string? value,
		bool usesTokenLogin,
		out string normalized,
		out string error)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			error = "请输入远程仓库地址。";
			return false;
		}
		if (!GitRemoteAddress.TryNormalize(value, out normalized))
		{
			error = "请输入有效的远程仓库地址。HTTP/HTTPS 地址不得内嵌用户名、密码或访问令牌。";
			return false;
		}
		if (usesTokenLogin &&
			(!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ||
			 !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
		{
			error = "令牌登录仅适用于 HTTPS 仓库地址。请使用 https:// 开头的地址。";
			normalized = string.Empty;
			return false;
		}

		error = string.Empty;
		return true;
	}
}
