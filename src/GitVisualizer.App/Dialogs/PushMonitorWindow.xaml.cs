using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class PushMonitorWindow : Window
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private bool isCompleted;

    public PushMonitorWindow(string remoteName, string remoteUrl, string branchName)
    {
        InitializeComponent();
        RemoteUrl = remoteUrl;
        TargetDescription = $"{branchName}  →  {remoteName}";
        DataContext = this;
        AddLine($"准备将分支 {branchName} 推送到远程 {remoteName}");
        AddLine($"目标地址：{remoteUrl}");
    }

    public ObservableCollection<PushMonitorEntry> Entries { get; } = [];

    public string TargetDescription { get; }

    public string RemoteUrl { get; }

    public void Report(GitPushProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(progress));
            return;
        }
        if (isCompleted)
        {
            return;
        }

        var stage = progress.Stage switch
        {
            GitPushProgressStage.Connecting => "连接远程仓库",
            GitPushProgressStage.Negotiating => "协商远程引用",
            GitPushProgressStage.Packing => "打包对象",
            GitPushProgressStage.Transferring => "上传对象",
            GitPushProgressStage.UpdatingTracking => "更新本地跟踪分支",
            _ => "正在推送"
        };
        StageText.Text = stage;

        if (progress.Total > 0)
        {
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = Math.Clamp(
                progress.Current * 100d / progress.Total,
                0,
                100);
            ProgressText.Text = progress.Stage == GitPushProgressStage.Transferring
                ? $"{progress.Current}/{progress.Total} 个对象 · {FormatBytes(progress.Bytes)}"
                : $"{progress.Current}/{progress.Total}";
        }
        else
        {
            TransferProgress.IsIndeterminate = true;
            ProgressText.Text = string.Empty;
        }

        var detail = progress.Message;
        if (progress.Stage == GitPushProgressStage.Transferring)
        {
            detail = progress.Total > 0
                ? $"已上传 {progress.Current}/{progress.Total} 个对象（{FormatBytes(progress.Bytes)}）"
                : $"正在上传对象（{FormatBytes(progress.Bytes)}）";
        }
        AddLine(string.IsNullOrWhiteSpace(detail) ? stage : $"{stage}：{detail}");
    }

    public void Complete(GitOperationResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Complete(result));
            return;
        }

        stopwatch.Stop();
        isCompleted = true;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = result.Success ? 100 : 0;
        ResultText.Text = result.Success ? "推送成功" : "推送失败";
        ResultText.Foreground = new SolidColorBrush(
            result.Success ? Color.FromRgb(34, 139, 94) : Color.FromRgb(224, 93, 93));
        ResultBadge.Background = new SolidColorBrush(
            result.Success ? Color.FromArgb(35, 66, 184, 131) : Color.FromArgb(35, 224, 93, 93));
        StageText.Text = result.Success ? "推送已完成" : "推送未完成";
        ProgressText.Text = result.Success ? "100%" : "失败";
        ElapsedText.Text = $"耗时 {stopwatch.Elapsed:mm\\:ss\\.fff}";

        AddLine(result.Success
            ? $"成功：{result.Summary}"
            : $"失败：{result.ErrorMessage ?? result.Summary}");
        foreach (var detail in result.Details)
        {
            AddLine($"详情：{detail}");
        }
        foreach (var warning in result.Warnings)
        {
            AddLine($"警告：{warning}");
        }
        AddLine($"命令：{result.EquivalentCommand}");

        CloseButton.IsEnabled = true;
        CloseButton.Focus();
    }

    private void AddLine(string message)
    {
        var entry = new PushMonitorEntry(DateTimeOffset.Now, message);
        Entries.Add(entry);
        ProgressList.ScrollIntoView(entry);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var display = (double)value;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }
        return $"{display:0.##} {units[unitIndex]}";
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}

public sealed record PushMonitorEntry(DateTimeOffset Timestamp, string Message);
