using System.Windows;
using System.Windows.Controls;
using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class CredentialWindowTests
{
    [Fact]
    public void SavedCredential_IsPrefilledAndCanBeRevealed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CredentialWindow? window = null;
            try
            {
                var saved = new RemoteCredential(
                    CredentialKind.HttpsToken,
                    "octocat",
                    "github_pat_test-secret",
                    Remember: true);
                window = new CredentialWindow(
                    "https://github.com/octocat/repository-a.git",
                    saved);
                window.Show();

                var passwordBox = Assert.IsType<PasswordBox>(window.FindName("TokenBox"));
                var visibleTokenBox = Assert.IsType<TextBox>(window.FindName("VisibleTokenBox"));
                var showTokenBox = Assert.IsType<CheckBox>(window.FindName("ShowTokenBox"));
                var status = Assert.IsType<TextBlock>(window.FindName("CredentialStatusText"));

                Assert.Equal("octocat", window.DisplayedUserName);
                Assert.Equal(
                    "https://github.com/octocat/repository-a.git",
                    window.DisplayedRepositoryAddress);
                Assert.Equal("github_pat_test-secret", window.DisplayedToken);
                Assert.False(window.IsTokenVisible);
                Assert.Equal(Visibility.Visible, passwordBox.Visibility);
                Assert.Equal(Visibility.Collapsed, visibleTokenBox.Visibility);
                Assert.Contains("已载入", status.Text);

                showTokenBox.IsChecked = true;

                Assert.True(window.IsTokenVisible);
                Assert.Equal(Visibility.Collapsed, passwordBox.Visibility);
                Assert.Equal(Visibility.Visible, visibleTokenBox.Visibility);
                Assert.Equal("github_pat_test-secret", visibleTokenBox.Text);
                Assert.Equal("github_pat_test-secret", window.Credential.Secret);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void MissingCredential_ShowsRepositoryWithoutLeakingAnotherRepository()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CredentialWindow? window = null;
            try
            {
                window = new CredentialWindow(
                    "https://github.com/octocat/repository-b.git");
                window.Show();

                var deleteButton = Assert.IsType<Button>(
                    window.FindName("DeleteCredentialButton"));
                Assert.Equal(
                    "https://github.com/octocat/repository-b.git",
                    window.DisplayedRepositoryAddress);
                Assert.Equal(string.Empty, window.DisplayedUserName);
                Assert.Equal(string.Empty, window.DisplayedToken);
                Assert.Equal(Visibility.Collapsed, deleteButton.Visibility);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
