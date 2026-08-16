using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;
using System.Windows.Controls;

namespace GitVisualizer.Tests;

public sealed class RemoteConfigurationWindowTests
{
    [Fact]
    public void ExistingRemote_IsListedAndPreselectedForEditing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            RemoteConfigurationWindow? window = null;
            try
            {
                var remote = new RemoteInfo(
                    "origin",
                    "https://github.com/Vsing-LUO/1111.git",
                    "https://github.com/Vsing-LUO/1111.git",
                    ["+refs/heads/*:refs/remotes/origin/*"],
                    []);
                window = new RemoteConfigurationWindow([remote]);
                window.Show();

                Assert.Equal(remote, Assert.Single(window.ConfiguredRemotes));
                Assert.Equal("origin", window.OriginalName);
                Assert.Equal("origin", window.RemoteName);
                Assert.Equal(
                    "https://github.com/Vsing-LUO/1111.git",
                    window.RemoteUrl);
                var deleteButton = Assert.IsType<Button>(
                    window.FindName("DeleteRemoteButton"));
                Assert.Equal("删除远程", deleteButton.Content);
                Assert.True(deleteButton.IsEnabled);
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
