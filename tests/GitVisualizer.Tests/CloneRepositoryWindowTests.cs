using System.Windows;
using System.Windows.Controls;
using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class CloneRepositoryWindowTests
{
    [Fact]
    public void PublicAndTokenOptions_CreateExpectedCredentials()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CloneRepositoryWindow? window = null;
            try
            {
                window = new CloneRepositoryWindow();
                window.Show();

                Assert.False(window.UsesTokenLogin);
                Assert.Null(window.Credential);
                var tokenPanel = Assert.IsType<Grid>(window.FindName("TokenFieldsPanel"));
                Assert.Equal(Visibility.Collapsed, tokenPanel.Visibility);

                Assert.IsType<RadioButton>(
                    window.FindName("TokenLoginOption")).IsChecked = true;
                Assert.Equal(Visibility.Visible, tokenPanel.Visibility);
                Assert.IsType<TextBox>(window.FindName("RepositoryUrlBox")).Text =
                    "https://github.com/example/private.git";
                Assert.IsType<TextBox>(window.FindName("UserNameBox")).Text = "octocat";
                Assert.IsType<PasswordBox>(window.FindName("TokenBox")).Password =
                    "github_pat_test-secret";

                var credential = Assert.IsType<RemoteCredential>(window.Credential);
                Assert.Equal(CredentialKind.HttpsToken, credential.Kind);
                Assert.Equal("octocat", credential.UserName);
                Assert.Equal("github_pat_test-secret", credential.Secret);
                Assert.True(credential.Remember);
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
