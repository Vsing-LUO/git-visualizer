using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class SafeWorkflowDialogTests
{
    [Fact]
    public void WorkflowDialogs_LoadWithExpectedInitialSelections()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var windows = new List<System.Windows.Window>();
            try
            {
                var remote = new RemoteInfo("upstream", "path", "path", [], []);
                var branch = new BranchInfo(
                    "upstream/main", "refs/remotes/upstream/main", "abc", false, true, null, 0, 0);
                windows.Add(new TagManagementWindow([new TagInfo("v1", "abc")]));
                windows.Add(new StashManagementWindow([
                    new StashInfo(0, "work", "abc", "def", DateTimeOffset.Now)]));
                windows.Add(new RebaseWindow(["main", "upstream/main"]));
                windows.Add(new ForcePushConfirmationWindow("upstream", "main"));
                var pull = new PullStrategyWindow([remote], [branch], remote, PullStrategy.Rebase);
                windows.Add(pull);

                foreach (var window in windows)
                {
                    window.Show();
                }
                Assert.Equal(remote, pull.SelectedRemote);
                Assert.Equal("main", pull.SelectedRemoteBranch);
                Assert.Equal(PullStrategy.Rebase, pull.SelectedStrategy);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                foreach (var window in windows)
                {
                    window.Close();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
