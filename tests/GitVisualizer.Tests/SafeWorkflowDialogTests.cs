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
                var firstCommit = new CommitNode(
                    "1111111111111111111111111111111111111111", "11111111", "first",
                    "user", "user@example.invalid", DateTimeOffset.Now, []);
                var secondCommit = new CommitNode(
                    "2222222222222222222222222222222222222222", "22222222", "second",
                    "user", "user@example.invalid", DateTimeOffset.Now, [firstCommit.Id]);
                var comparison = new CommitComparisonWindow(
                    [secondCommit, firstCommit],
                    firstCommit.Id,
                    secondCommit.Id);
                windows.Add(comparison);
                var recoveryPoint = new RecoveryPoint(
                    "recovery-id", "repository", "discard", firstCommit.Id,
                    "refs/gitvisualizer/recovery/test", "archive.zip",
                    new DateTimeOffset(2026, 8, 24, 6, 30, 0, TimeSpan.Zero),
                    128, true);
                windows.Add(new RecoveryCenterWindow([recoveryPoint]));

                foreach (var window in windows)
                {
                    window.Show();
                }
                Assert.Equal(remote, pull.SelectedRemote);
                Assert.Equal("main", pull.SelectedRemoteBranch);
                Assert.Equal(PullStrategy.Rebase, pull.SelectedStrategy);
                Assert.Equal(firstCommit, comparison.OldCommit);
                Assert.Equal(secondCommit, comparison.NewCommit);
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
