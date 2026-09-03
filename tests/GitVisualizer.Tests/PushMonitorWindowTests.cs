using GitVisualizer.App.Dialogs;
using GitVisualizer.Core;
using System.Windows.Controls;

namespace GitVisualizer.Tests;

public sealed class PushMonitorWindowTests
{
    [Fact]
    public void ProgressAndSuccessfulResult_AreRenderedInTheMonitor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            PushMonitorWindow? window = null;
            try
            {
                window = new PushMonitorWindow(
                    "backup",
                    "https://example.invalid/repository.git",
                    "main");
                window.Show();
                window.Report(new GitPushProgress(
                    GitPushProgressStage.Transferring,
                    3,
                    4,
                    2048));
                window.Complete(GitOperationResult.Ok(
                    "push",
                    "推送完成",
                    "git push backup"));

                var stageText = Assert.IsType<TextBlock>(
                    window.FindName("StageText"));
                var resultText = Assert.IsType<TextBlock>(
                    window.FindName("ResultText"));
                var progressBar = Assert.IsType<ProgressBar>(
                    window.FindName("TransferProgress"));
                var closeButton = Assert.IsType<Button>(
                    window.FindName("CloseButton"));

                Assert.Equal("推送已完成", stageText.Text);
                Assert.Equal("推送成功", resultText.Text);
                Assert.Equal(100, progressBar.Value);
                Assert.True(closeButton.IsEnabled);
                Assert.Contains(
                    window.Entries,
                    entry => entry.Message.Contains("已上传 3/4 个对象"));
                Assert.Contains(
                    window.Entries,
                    entry => entry.Message == "成功：推送完成");
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
    public void FailedNonFastForwardPush_ShowsChineseExplanationAndSafeSuggestion()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            PushMonitorWindow? window = null;
            try
            {
                window = new PushMonitorWindow(
                    "origin",
                    "https://example.invalid/repository.git",
                    "main");
                window.Show();
                window.Complete(GitOperationResult.Fail(
                    "push",
                    "git push origin",
                    new InvalidOperationException(
                        "cannot push non-fastforwardable reference")));

                Assert.Contains(
                    window.Entries,
                    entry => entry.Message.StartsWith("中文解释：") &&
                             entry.Message.Contains("远程分支包含本地尚未拥有的提交"));
                Assert.Contains(
                    window.Entries,
                    entry => entry.Message.StartsWith("处理建议：") &&
                             entry.Message.Contains("拉取合并或变基"));
                Assert.DoesNotContain(
                    window.Entries,
                    entry => entry.Message.Contains("强制推送"));
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
    public void UnknownPushFailure_StillShowsGenericChineseExplanation()
    {
        var result = GitOperationResult.Fail(
            "push",
            "git push origin",
            new InvalidOperationException("unexpected vendor-specific failure"));

        var explanation = PushFailureExplainer.Explain(result);

        Assert.Contains("未能完成推送", explanation.Reason);
        Assert.Contains("原始错误", explanation.Suggestion);
    }
}
