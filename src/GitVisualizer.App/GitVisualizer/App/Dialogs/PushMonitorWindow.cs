using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

public partial class PushMonitorWindow : Window, IComponentConnector
{
	private readonly Stopwatch stopwatch = Stopwatch.StartNew();

	private bool isCompleted;

	public ObservableCollection<PushMonitorEntry> Entries { get; } = new ObservableCollection<PushMonitorEntry>();

	public string TargetDescription { get; }

	public string RemoteUrl { get; }

	public PushMonitorWindow(string remoteName, string remoteUrl, string branchName)
	{
		InitializeComponent();
		RemoteUrl = remoteUrl;
		TargetDescription = branchName + "  →  " + remoteName;
		base.DataContext = this;
		AddLine("准备将分支 " + branchName + " 推送到远程 " + remoteName);
		AddLine("目标地址：" + remoteUrl);
	}

	public void Report(GitPushProgress progress)
	{
		if (!base.Dispatcher.CheckAccess())
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				Report(progress);
			});
		}
		else if (!isCompleted)
		{
			string text = progress.Stage switch
			{
				GitPushProgressStage.Connecting => "连接远程仓库",
				GitPushProgressStage.Negotiating => "协商远程引用",
				GitPushProgressStage.Packing => "打包对象",
				GitPushProgressStage.Transferring => "上传对象",
				GitPushProgressStage.UpdatingTracking => "更新本地跟踪分支",
				_ => "正在推送",
			};
			StageText.Text = text;
			if (progress.Total > 0)
			{
				TransferProgress.IsIndeterminate = false;
				TransferProgress.Value = Math.Clamp((double)progress.Current * 100.0 / (double)progress.Total, 0.0, 100.0);
				ProgressText.Text = ((progress.Stage == GitPushProgressStage.Transferring) ? $"{progress.Current}/{progress.Total} 个对象 · {FormatBytes(progress.Bytes)}" : $"{progress.Current}/{progress.Total}");
			}
			else
			{
				TransferProgress.IsIndeterminate = true;
				ProgressText.Text = string.Empty;
			}
			string text2 = progress.Message;
			if (progress.Stage == GitPushProgressStage.Transferring)
			{
				text2 = ((progress.Total > 0) ? $"已上传 {progress.Current}/{progress.Total} 个对象（{FormatBytes(progress.Bytes)}）" : ("正在上传对象（" + FormatBytes(progress.Bytes) + "）"));
			}
			AddLine(string.IsNullOrWhiteSpace(text2) ? text : (text + "：" + text2));
		}
	}

	public void Complete(GitOperationResult result)
	{
		if (!base.Dispatcher.CheckAccess())
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				Complete(result);
			});
			return;
		}
		stopwatch.Stop();
		isCompleted = true;
		TransferProgress.IsIndeterminate = false;
		TransferProgress.Value = (result.Success ? 100 : 0);
		ResultText.Text = (result.Success ? "推送成功" : "推送失败");
		ResultText.Foreground = new SolidColorBrush(result.Success ? Color.FromRgb(34, 139, 94) : Color.FromRgb(224, 93, 93));
		ResultBadge.Background = new SolidColorBrush(result.Success ? Color.FromArgb(35, 66, 184, 131) : Color.FromArgb(35, 224, 93, 93));
		StageText.Text = (result.Success ? "推送已完成" : "推送未完成");
		ProgressText.Text = (result.Success ? "100%" : "失败");
		ElapsedText.Text = $"耗时 {stopwatch.Elapsed:mm\\:ss\\.fff}";
		AddLine(result.Success ? ("成功：" + result.Summary) : ("失败：" + (result.ErrorMessage ?? result.Summary)));
		if (!result.Success)
		{
			PushFailureExplanation explanation = PushFailureExplainer.Explain(result);
			AddLine("中文解释：" + explanation.Reason);
			AddLine("处理建议：" + explanation.Suggestion);
		}
		foreach (string detail in result.Details)
		{
			AddLine("详情：" + detail);
		}
		foreach (string warning in result.Warnings)
		{
			AddLine("警告：" + warning);
		}
		AddLine("命令：" + result.EquivalentCommand);
		CloseButton.IsEnabled = true;
		CloseButton.Focus();
	}

	private void AddLine(string message)
	{
		PushMonitorEntry item = new PushMonitorEntry(DateTimeOffset.Now, message);
		Entries.Add(item);
		ProgressList.ScrollIntoView(item);
	}

	private static string FormatBytes(long bytes)
	{
		string[] array = new string[4] { "B", "KB", "MB", "GB" };
		long num = Math.Max(0L, bytes);
		int num2 = 0;
		double num3 = num;
		while (num3 >= 1024.0 && num2 < array.Length - 1)
		{
			num3 /= 1024.0;
			num2++;
		}
		return $"{num3:0.##} {array[num2]}";
	}

	private void Close_OnClick(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
