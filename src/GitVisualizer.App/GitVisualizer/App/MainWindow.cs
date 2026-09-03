using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GitVisualizer.App.Controls;
using GitVisualizer.App.Dialogs;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using Microsoft.Win32;

namespace GitVisualizer.App;

public partial class MainWindow : Window, IComponentConnector
{
	private readonly record struct FeaturePanelState(
		int TabIndex,
		string? ConflictPath,
		string ConflictResultText);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ShellFileInfo
	{
		public nint IconHandle;

		public int IconIndex;

		public uint Attributes;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string? DisplayName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
		public string? TypeName;
	}

	private const int DwmWindowAttributeCaptionColor = 35;

	private const int DwmWindowAttributeTextColor = 36;

	private const int BlackColorRef = 0;

	private const int WhiteColorRef = 16777215;

	public const double DefaultWindowWidth = 1624.0;

	public const double DefaultWindowHeight = 968.0;

	public const double DefaultRepositoryColumnWidth = 350.0;

	public const double DefaultDetailsColumnWidth = 420.0;

	public const double DefaultStagingPanelHeight = 132.0;

	public const double BranchItemHeight = 30.0;

	public const int MinimumVisibleBranchCount = 3;

	public const double MinimumBranchListHeight = 92.0;

	public const double MinimumFileWorkspaceHeight = 180.0;

	public const double MaximumFileWorkspaceHeight = 280.0;

	public const double DefaultFileWorkspaceHeight = 230.0;

	private const double CompactCommitMessageThreshold = 0.4;

	private const double ExpandedCommitMessageThreshold = 0.46;

	private static readonly HashSet<string> BuiltInNewFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".docx", ".cs", ".json", ".xml" };

	private readonly MainWindowViewModel viewModel;

	private static readonly string[] FeatureTabNames = ["差异", "编辑器", "详情", "冲突", "操作日志"];

	private FrameworkElement[] featureZoomScopes = [];

	private readonly double[] featureZoomLevels = [1.0, 1.0, 1.0, 1.0, 1.0];

	private readonly DispatcherTimer featureToastTimer;

	private FeaturePanelWindow? featurePanelWindow;

	private FeaturePanelState? featurePanelStateBeforeClose;

	private bool isMainWindowShuttingDown;

	private FileTreeItem? selectedTreeItem;

	private bool isRecoveryCenterOpen;

	private bool closeApproved;

	private bool closeGuardRunning;

	private bool isCompactCommitMessageLayout;

	private bool hasInitializedDefaultStagingHeight;

	private ListBox? fileSelectionList;

	private Canvas? fileSelectionOverlay;

	private Rectangle? fileSelectionRectangle;

	private Point fileSelectionStart;

	private bool fileSelectionDragging;

	private bool isBulkSelectingFiles;

	private bool suppressFileTreeSelection;

	private string? rejectedBranchSelectionName;

	private readonly HashSet<string> acceptedUnstagedSelection = new(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> acceptedStagedSelection = new(StringComparer.OrdinalIgnoreCase);

	private HashSet<object> fileSelectionBaseItems = new HashSet<object>();

	private const uint FileAttributeNormal = 128u;

	private const uint ShellGetFileInfoIcon = 256u;

	private const uint ShellGetFileInfoSmallIcon = 1u;

	private const uint ShellGetFileInfoUseFileAttributes = 16u;

	public static GridLength DefaultStagingPanelGridLength { get; } = new GridLength(132.0);

	public static GridLength DefaultFileWorkspaceGridLength { get; } = new GridLength(230.0);

	public MainWindow(MainWindowViewModel viewModel)
	{
		InitializeComponent();
		this.viewModel = viewModel;
		featureZoomScopes = [DiffZoomScope, EditorZoomScope, DetailsZoomScope, ConflictZoomScope, LogZoomScope];
		foreach (FrameworkElement scope in featureZoomScopes)
		{
			PanelTextZoom.Attach(scope, ReferenceEquals(scope, DiffZoomScope));
		}
		featureToastTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(1600.0)
		};
		featureToastTimer.Tick += FeatureToastTimer_OnTick;
		base.DataContext = viewModel;
		FeaturePanelHost.DataContext = viewModel;
		base.PreviewKeyDown += MainWindow_OnPreviewKeyDown;
		base.Loaded += MainWindow_OnLoaded;
		base.SourceInitialized += MainWindow_OnSourceInitialized;
		base.Closing += MainWindow_OnClosing;
		viewModel.ConflictDetected += ViewModel_OnConflictDetected;
	}

	private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
	{
		if (closeApproved)
		{
			return;
		}

		e.Cancel = true;
		if (closeGuardRunning)
		{
			return;
		}
		closeGuardRunning = true;
		DeferCloseGuard(base.Dispatcher, CompleteCloseGuardAsync);
	}

	internal static void DeferCloseGuard(Dispatcher dispatcher, Func<Task> guard)
	{
		dispatcher.BeginInvoke(
			DispatcherPriority.ContextIdle,
			new Action(() => _ = guard()));
	}

	private async Task CompleteCloseGuardAsync()
	{
		try
		{
			if (await viewModel.PrepareForCloseAsync())
			{
				isMainWindowShuttingDown = true;
				CloseFeaturePanelWindow();
				closeApproved = true;
				Close();
			}
		}
		finally
		{
			closeGuardRunning = false;
		}
	}

	private Window DialogOwner => featurePanelWindow?.IsActive == true ? featurePanelWindow : this;

	private void FeatureFullscreenButton_OnClick(object sender, RoutedEventArgs e)
	{
		if (featurePanelWindow == null)
		{
			OpenFeaturePanelWindow();
		}
		else
		{
			CloseFeaturePanelWindow();
		}
	}

	private void ShowFeaturePanelWindow_OnClick(object sender, RoutedEventArgs e)
	{
		if (featurePanelWindow == null)
		{
			OpenFeaturePanelWindow();
			return;
		}

		if (featurePanelWindow.WindowState == WindowState.Minimized)
		{
			featurePanelWindow.WindowState = WindowState.Maximized;
		}
		featurePanelWindow.Activate();
	}

	private void OpenFeaturePanelWindow()
	{
		if (featurePanelWindow != null)
		{
			ShowFeaturePanelWindow_OnClick(this, new RoutedEventArgs());
			return;
		}

		FeaturePanelState panelState = CaptureFeaturePanelState();
		FeaturePanelWindow window = new FeaturePanelWindow
		{
			Owner = this,
			DataContext = viewModel,
			ShowInTaskbar = true,
			Title = BuildFeaturePanelWindowTitle()
		};
		window.EscapeRequested += FeaturePanelWindow_OnEscapeRequested;
		window.Closing += FeaturePanelWindow_OnClosing;
		window.Closed += FeaturePanelWindow_OnClosed;

		try
		{
			FeaturePanelDock.Children.Remove(FeaturePanelHost);
			FeaturePanelDetachedPlaceholder.Visibility = Visibility.Visible;
			window.PanelContent = FeaturePanelHost;
			featurePanelWindow = window;
			UpdateFeatureFullscreenButton();
			window.Show();
			window.WindowState = WindowState.Maximized;
			RestoreFeaturePanelState(panelState);
			ApplyAllFeatureZoomScales();
			window.Activate();
			ShowFeatureToast("已在独立窗口展开 · Esc 返回主界面");
		}
		catch (Exception exception)
		{
			window.Closing -= FeaturePanelWindow_OnClosing;
			window.PanelContent = null;
			RestoreFeaturePanelHost();
			RestoreFeaturePanelState(panelState);
			featurePanelWindow = null;
			UpdateFeatureFullscreenButton();
			ApplyAllFeatureZoomScales();
			MessageBox.Show(DialogOwner, "无法打开独立功能区窗口：" + exception.Message, "展开功能区", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void CloseFeaturePanelWindow()
	{
		FeaturePanelWindow? window = featurePanelWindow;
		if (window == null)
		{
			return;
		}

		window.Close();
	}

	private void FeaturePanelWindow_OnEscapeRequested(object? sender, EventArgs e)
	{
		CloseFeaturePanelWindow();
	}

	private void FeaturePanelWindow_OnClosing(object? sender, CancelEventArgs e)
	{
		featurePanelStateBeforeClose = CaptureFeaturePanelState();
	}

	private void FeaturePanelWindow_OnClosed(object? sender, EventArgs e)
	{
		if (sender is not FeaturePanelWindow window)
		{
			return;
		}

		FeaturePanelState panelState = featurePanelStateBeforeClose ?? CaptureFeaturePanelState();
		featurePanelStateBeforeClose = null;
		window.EscapeRequested -= FeaturePanelWindow_OnEscapeRequested;
		window.Closing -= FeaturePanelWindow_OnClosing;
		window.Closed -= FeaturePanelWindow_OnClosed;
		window.PanelContent = null;
		if (ReferenceEquals(featurePanelWindow, window))
		{
			featurePanelWindow = null;
		}

		RestoreFeaturePanelHost();
		RestoreFeaturePanelState(panelState);
		UpdateFeatureFullscreenButton();
		ApplyAllFeatureZoomScales();
		if (!isMainWindowShuttingDown)
		{
			Activate();
			ShowFeatureToast("功能区已返回主界面");
		}
	}

	private void RestoreFeaturePanelHost()
	{
		if (FeaturePanelHost.Parent == null)
		{
			FeaturePanelDock.Children.Add(FeaturePanelHost);
		}
		FeaturePanelDetachedPlaceholder.Visibility = Visibility.Collapsed;
	}

	private FeaturePanelState CaptureFeaturePanelState() =>
		new FeaturePanelState(
			viewModel.SelectedRightTabIndex,
			viewModel.SelectedConflict?.Path,
			viewModel.ConflictResultText);

	private void RestoreFeaturePanelState(FeaturePanelState state)
	{
		FeaturePanelHost.DataContext = viewModel;
		int tabIndex = Math.Clamp(state.TabIndex, 0, FeatureTabNames.Length - 1);
		ConflictFile? conflict = ResolveConflictSelection(state.ConflictPath, viewModel.Conflicts);
		viewModel.SelectConflict(conflict);
		if (conflict != null &&
			conflict.Path.Equals(state.ConflictPath, StringComparison.OrdinalIgnoreCase))
		{
			viewModel.ConflictResultText = state.ConflictResultText;
		}
		viewModel.SelectedRightTabIndex = tabIndex;
		FeatureTabs.SelectedIndex = tabIndex;
		ConflictList.SelectedItem = conflict;
	}

	internal static ConflictFile? ResolveConflictSelection(
		string? preferredPath,
		IReadOnlyList<ConflictFile> conflicts) =>
		conflicts.FirstOrDefault(conflict =>
			conflict.Path.Equals(preferredPath, StringComparison.OrdinalIgnoreCase)) ??
		conflicts.FirstOrDefault();

	private void UpdateFeatureFullscreenButton()
	{
		bool detached = featurePanelWindow != null;
		FeatureExpandIcon.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
		FeatureRestoreIcon.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
		FeatureFullscreenButton.ToolTip = detached ? "返回主界面（Esc）" : "在独立最大化窗口中展开功能区";
		AutomationProperties.SetName(FeatureFullscreenButton, detached ? "将功能区返回主界面" : "在独立窗口展开功能区");
	}

	private void FeatureTabs_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			return;
		}

		int tabIndex = FeatureTabs.SelectedIndex;
		if (tabIndex < 0 || tabIndex >= featureZoomScopes.Length || e.Delta == 0)
		{
			return;
		}

		e.Handled = true;
		bool detached = featurePanelWindow != null;
		bool hasZoomableEditorContent = viewModel.CurrentDocument != null &&
			!viewModel.IsExternalOnlyDocument;
		if (!PanelTextZoom.IsZoomInputAllowed(tabIndex, detached, hasZoomableEditorContent))
		{
			return;
		}

		double current = featureZoomLevels[tabIndex];
		double next = PanelTextZoom.CalculateNextScale(current, e.Delta);
		if (Math.Abs(next - current) < 0.001)
		{
			ShowFeatureToast(e.Delta > 0 ? "已达最大字号（200%）" : "已是最小字号（100%）");
			return;
		}

		featureZoomLevels[tabIndex] = next;
		ApplyFeatureZoomScale(tabIndex);
	}

	private void FeatureTabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (featurePanelWindow != null)
		{
			featurePanelWindow.Title = BuildFeaturePanelWindowTitle();
		}
		if (FeatureTabs.SelectedIndex >= 0 && FeatureTabs.SelectedIndex < featureZoomScopes.Length)
		{
			ApplyFeatureZoomScale(FeatureTabs.SelectedIndex);
		}
	}

	private string BuildFeaturePanelWindowTitle()
	{
		int tabIndex = FeatureTabs.SelectedIndex;
		string tabName = tabIndex >= 0 && tabIndex < FeatureTabNames.Length ? FeatureTabNames[tabIndex] : "功能区";
		return $"GitVisualizer · {tabName}";
	}

	private void ApplyAllFeatureZoomScales()
	{
		for (int tabIndex = 0; tabIndex < featureZoomScopes.Length; tabIndex++)
		{
			ApplyFeatureZoomScale(tabIndex);
		}
	}

	private void ApplyFeatureZoomScale(int tabIndex)
	{
		double effectiveScale = PanelTextZoom.GetEffectiveScale(tabIndex, featurePanelWindow != null, featureZoomLevels[tabIndex]);
		PanelTextZoom.SetScale(featureZoomScopes[tabIndex], effectiveScale);
	}

	private void ShowFeatureToast(string message)
	{
		featureToastTimer.Stop();
		FeaturePanelToast.BeginAnimation(OpacityProperty, null);
		FeaturePanelToast.Opacity = 1.0;
		FeaturePanelToastText.Text = message;
		FeaturePanelToast.Visibility = Visibility.Visible;
		featureToastTimer.Start();
	}

	private void FeatureToastTimer_OnTick(object? sender, EventArgs e)
	{
		featureToastTimer.Stop();
		DoubleAnimation animation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180.0))
		{
			FillBehavior = FillBehavior.Stop
		};
		animation.Completed += (_, _) =>
		{
			FeaturePanelToast.Visibility = Visibility.Collapsed;
			FeaturePanelToast.Opacity = 1.0;
		};
		FeaturePanelToast.BeginAnimation(OpacityProperty, animation);
	}

	private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
	{
		nint handle = new WindowInteropHelper(this).Handle;
		int pvAttribute = 0;
		int pvAttribute2 = 16777215;
		DwmSetWindowAttribute(handle, 35, ref pvAttribute, Marshal.SizeOf<int>());
		DwmSetWindowAttribute(handle, 36, ref pvAttribute2, Marshal.SizeOf<int>());
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

	private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
	{
		ApplyStartupLayoutBaseline();
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InitializeDefaultStagingPanelHeight));
	}

	private void ApplyStartupLayoutBaseline()
	{
		base.WindowState = WindowState.Normal;
		base.Width = DefaultWindowWidth;
		base.Height = DefaultWindowHeight;
		RepositoryColumn.Width = new GridLength(DefaultRepositoryColumnWidth);
		DetailsColumn.Width = new GridLength(DefaultDetailsColumnWidth);
		FileWorkspaceRow.Height = DefaultFileWorkspaceGridLength;
		StagingPanelRow.Height = DefaultStagingPanelGridLength;
		viewModel.IsCommitGraphCollapsed = false;
		viewModel.SelectedRightTabIndex = 1;
	}

	private void InitializeDefaultStagingPanelHeight()
	{
		if (!hasInitializedDefaultStagingHeight)
		{
			hasInitializedDefaultStagingHeight = true;
			double value = CalculateCompactBoundaryPanelHeight(Math.Max(0.0, CommitPanel.ActualHeight - CommitMessageRow.ActualHeight));
			StagingPanelRow.Height = new GridLength(Math.Clamp(value, StagingPanelRow.MinHeight, StagingPanelRow.MaxHeight));
		}
	}

	private void CommitPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateCommitMessageLayout));
	}

	private void UpdateCommitMessageLayout()
	{
		bool flag = ShouldUseCompactCommitMessageLayout(CommitPanel.ActualHeight, CommitMessageRow.ActualHeight, isCompactCommitMessageLayout);
		if (flag != isCompactCommitMessageLayout)
		{
			isCompactCommitMessageLayout = flag;
			Grid.SetRow(CommitMessageTextBox, (!flag) ? 1 : 0);
			CommitMessageTextBox.Margin = (flag ? new Thickness(64.0, 7.0, 10.0, 7.0) : new Thickness(10.0, 8.0, 10.0, 4.0));
			CommitMessageTextBox.Height = (flag ? 32.0 : double.NaN);
			CommitMessageTextBox.MinHeight = (flag ? 32 : 34);
			CommitMessageTextBox.Padding = (flag ? new Thickness(9.0, 2.0, 9.0, 2.0) : new Thickness(9.0, 7.0, 9.0, 7.0));
			CommitMessageTextBox.AcceptsReturn = !flag;
			CommitMessageTextBox.TextWrapping = (flag ? TextWrapping.NoWrap : TextWrapping.Wrap);
			CommitMessageTextBox.VerticalScrollBarVisibility = ((!flag) ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
			CommitMessageTextBox.VerticalContentAlignment = VerticalAlignment.Center;
			CommitMessageHint.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	internal static bool ShouldUseCompactCommitMessageLayout(double panelHeight, double messageRowHeight, bool isCurrentlyCompact)
	{
		if (panelHeight <= 0.0 || !double.IsFinite(panelHeight))
		{
			return false;
		}
		double num = Math.Max(0.0, messageRowHeight) / panelHeight;
		double num2 = (isCurrentlyCompact ? 0.46 : 0.4);
		return num < num2;
	}

	internal static double CalculateCompactBoundaryPanelHeight(double fixedRowsHeight)
	{
		if (fixedRowsHeight <= 0.0 || !double.IsFinite(fixedRowsHeight))
		{
			return 132.0;
		}
		double a = fixedRowsHeight / 0.54;
		return Math.Max(0.0, Math.Ceiling(a) - 1.0);
	}

	private void ViewModel_OnConflictDetected(object? sender, ConflictDetectedEventArgs e)
	{
		MessageBox.Show(DialogOwner, $"{e.OperationName}过程中检测到 {e.ConflictCount} 个冲突文件，当前 Git 操作已暂停。\n\n" + "请确认此提示，然后在“冲突”页面逐个检查并解决冲突。所有冲突解决前，请勿继续当前操作。", "检测到 Git 冲突", MessageBoxButton.OK, MessageBoxImage.Exclamation);
	}

	private async void OpenRepository_OnClick(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择 Git 仓库"
		};
		if (openFolderDialog.ShowDialog(this) == true)
		{
			await TryOpenOrInitializeAsync(openFolderDialog.FolderName);
		}
	}

	private async void InitializeRepository_OnClick(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择要初始化的文件夹"
		};
		if (openFolderDialog.ShowDialog(this) == true)
		{
			await TryOpenOrInitializeAsync(openFolderDialog.FolderName);
		}
	}

	private async void CloneRepository_OnClick(object sender, RoutedEventArgs e)
	{
		CloneRepositoryWindow cloneDialog = new CloneRepositoryWindow
		{
			Owner = this
		};
		if (cloneDialog.ShowDialog() != true)
		{
			return;
		}
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择克隆目标的父文件夹"
		};
		if (openFolderDialog.ShowDialog(this) != true)
		{
			return;
		}
		string path = GuessRepositoryName(cloneDialog.RepositoryUrl);
		string destination = System.IO.Path.Combine(openFolderDialog.FolderName, path);
		GitOperationResult gitOperationResult = await viewModel.CloneRepositoryAsync(cloneDialog.RepositoryUrl, destination, cloneDialog.Credential);
		if (gitOperationResult.Success)
		{
			RemoteCredential credential = cloneDialog.Credential;
			if ((object)credential != null && credential.Remember)
			{
				await viewModel.SaveCloneCredentialAsync(cloneDialog.RepositoryUrl, credential);
			}
			MessageBox.Show(this, "远程仓库已成功克隆。\n\n克隆位置：\n" + System.IO.Path.GetFullPath(destination), "克隆完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		else
		{
			string text = gitOperationResult.ErrorMessage ?? gitOperationResult.Summary;
			if (text.Contains("authentication required but no callback set", StringComparison.OrdinalIgnoreCase))
			{
				text = "远程仓库要求身份验证。请重新克隆并选择“令牌登录”，然后填写用户名和访问令牌。";
			}
			MessageBox.Show(this, text, "克隆失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async void ConfigureRemote_OnClick(object sender, RoutedEventArgs e)
	{
		if (!viewModel.HasRepository)
		{
			MessageBox.Show(this, "请先打开一个本地仓库。", "配置远程仓库");
			return;
		}
		RemoteConfigurationWindow dialog = new RemoteConfigurationWindow(viewModel.Remotes)
		{
			Owner = this
		};
		if (dialog.ShowDialog() == true)
		{
			string remoteToRemove = dialog.RemoteToRemove;
			GitOperationResult gitOperationResult = ((remoteToRemove == null) ? (await viewModel.ConfigureRemoteAsync(dialog.OriginalName, dialog.RemoteName, dialog.RemoteUrl)) : (await viewModel.RemoveRemoteAsync(remoteToRemove)));
			GitOperationResult gitOperationResult2 = gitOperationResult;
			if (!gitOperationResult2.Success)
			{
				MessageBox.Show(this, gitOperationResult2.ErrorMessage ?? gitOperationResult2.Summary, (dialog.RemoteToRemove == null) ? "无法保存远程仓库" : "无法删除远程仓库", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private async void RecoveryCenter_OnClick(object sender, RoutedEventArgs e)
	{
		if (!viewModel.HasRepository || isRecoveryCenterOpen)
		{
			return;
		}
		isRecoveryCenterOpen = true;
		try
		{
			RecoveryCenterWindow recoveryCenterWindow = new RecoveryCenterWindow(await viewModel.GetRecoveryPointsAsync())
			{
				Owner = this
			};
			if (recoveryCenterWindow.ShowDialog() == true && (object)recoveryCenterWindow.SelectedPoint != null)
			{
				RecoveryPoint selectedPoint = recoveryCenterWindow.SelectedPoint;
				if (recoveryCenterWindow.DeleteRequested)
				{
					GitOperationResult deleteResult = await viewModel.DeleteRecoveryPointAsync(selectedPoint);
					MessageBox.Show(this, deleteResult.Success ? deleteResult.Summary : (deleteResult.ErrorMessage ?? deleteResult.Summary), deleteResult.Success ? "恢复点已删除" : "删除恢复点失败", MessageBoxButton.OK, deleteResult.Success ? MessageBoxImage.Asterisk : MessageBoxImage.Hand);
				}
				else if (MessageBox.Show(this, $"恢复到 {selectedPoint.LocalCreatedAt:yyyy-MM-dd HH:mm:ss} 的状态？\n\n" + "程序会先保存当前现场，然后创建并切换到独立 recovered/... 分支，同时恢复当时的工作区和暂存区。", "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
				{
					GitOperationResult gitOperationResult = await viewModel.RestoreRecoveryPointAsync(selectedPoint);
					MessageBox.Show(this, gitOperationResult.Success ? (gitOperationResult.Summary + "\n\n" + string.Join("\n", gitOperationResult.Details)) : (gitOperationResult.ErrorMessage ?? gitOperationResult.Summary), gitOperationResult.Success ? "恢复完成" : "恢复失败", MessageBoxButton.OK, gitOperationResult.Success ? MessageBoxImage.Asterisk : MessageBoxImage.Hand);
				}
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "无法打开恢复中心：" + ex.Message, "恢复中心", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			isRecoveryCenterOpen = false;
		}
	}

	private async void Push_OnClick(object sender, RoutedEventArgs e)
	{
		RemoteInfo selectedRemote = viewModel.SelectedRemote;
		if ((object)selectedRemote == null)
		{
			MessageBox.Show(this, "请先配置远程仓库，并在“推送”按钮左侧选择推送目标。", "选择推送目标", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		else
		{
			await RunPushAsync(selectedRemote, forceWithLease: false);
		}
	}

	private async Task RunPushAsync(RemoteInfo remote, bool forceWithLease)
	{
		string branchName = viewModel.Head?.BranchName ?? "当前 HEAD";
		PushMonitorWindow monitor = new PushMonitorWindow(remote.Name, remote.PushUrl, branchName)
		{
			Owner = this
		};
		monitor.Show();
		Progress<GitPushProgress> progress = new Progress<GitPushProgress>(monitor.Report);
		monitor.Complete(await viewModel.PushToRemoteAsync(remote, progress, forceWithLease));
	}

	private async Task<bool> TryOpenOrInitializeAsync(string path)
	{
		_ = 2;
		try
		{
			if (await viewModel.IsRepositoryAsync(path))
			{
				return await viewModel.OpenRepositoryAsync(path);
			}
			if (MessageBox.Show(this, "此文件夹尚未初始化为 Git 仓库，是否立即初始化并打开？\n\n" + path, "初始化仓库", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return false;
			}
			GitIdentity? inheritedIdentity = await viewModel.GetDefaultIdentityAsync();
			GitIdentity? localIdentity = null;
			if (inheritedIdentity == null)
			{
				string? name = Prompt("Git 身份", "尚未配置 Git 身份，请输入用户名：", string.Empty);
				if (name == null)
				{
					return false;
				}
				string? email = Prompt("Git 身份", "请输入邮箱：", string.Empty);
				if (email == null)
				{
					return false;
				}
				GitIdentity enteredIdentity = new GitIdentity(name, email);
				MessageBoxResult scope = MessageBox.Show(this,
					"选择“是”保存为全局默认身份；选择“否”只保存到当前新仓库。",
					"Git 身份保存范围", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
				if (scope == MessageBoxResult.Cancel)
				{
					return false;
				}
				if (scope == MessageBoxResult.Yes)
				{
					GitOperationResult identityResult = await viewModel.ConfigureGlobalIdentityAsync(enteredIdentity);
					if (!identityResult.Success)
					{
						MessageBox.Show(this, identityResult.ErrorMessage ?? "无法保存全局 Git 身份。", "Git 身份", MessageBoxButton.OK, MessageBoxImage.Hand);
						return false;
					}
				}
				else
				{
					localIdentity = enteredIdentity;
				}
			}
			GitOperationResult gitOperationResult = await viewModel.InitializeRepositoryAsync(path, localIdentity);
			if (!gitOperationResult.Success)
			{
				MessageBox.Show(this, gitOperationResult.ErrorMessage ?? "仓库初始化失败。", "无法初始化仓库", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			return gitOperationResult.Success;
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "无法打开所选文件夹：\n\n" + ex.Message, "打开仓库失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			return false;
		}
	}

	private async void RecentRepositories_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (((ListBox)sender).SelectedItem is string text && !PathsEqual(text, viewModel.ActiveRepositoryPath))
		{
			if (viewModel.IsBusy)
			{
				viewModel.SelectedRepository = (viewModel.HasRepository ? viewModel.ActiveRepositoryPath : null);
			}
			else if (!(await TryOpenOrInitializeAsync(text)))
			{
				viewModel.SelectedRepository = (viewModel.HasRepository ? viewModel.ActiveRepositoryPath : null);
			}
		}
	}

	private void RecentRepositories_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is ListBox itemsControl && e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(itemsControl, element) is ListBoxItem listBoxItem)
		{
			listBoxItem.IsSelected = true;
			listBoxItem.Focus();
		}
	}

	private async void RemoveRepository_OnClick(object sender, RoutedEventArgs e)
	{
		if (RecentRepositoriesList.SelectedItem is string text && MessageBox.Show(this, "只把以下仓库从左侧导航列表中移除？\n\n" + text + "\n\n仓库目录、项目文件和 .git 数据都不会被删除。", "从仓库列表中移除", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await viewModel.RemoveRecentRepositoryAsync(text);
		}
	}

	private async void RepositorySort_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (((ComboBox)sender).SelectedItem is string mode)
		{
			await viewModel.SortRepositoriesAsync(mode);
		}
	}

	private void OpenRepositoryFolder_OnClick(object sender, RoutedEventArgs e)
	{
		string text = RecentRepositoriesList.SelectedItem as string;
		if (string.IsNullOrWhiteSpace(text) && viewModel.HasRepository)
		{
			text = viewModel.ActiveRepositoryPath;
		}
		if (string.IsNullOrWhiteSpace(text) || !Directory.Exists(text))
		{
			MessageBox.Show(this, "所选仓库目录不存在，请重新打开仓库。", "打开仓库目录", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = System.IO.Path.GetFullPath(text),
				UseShellExecute = true
			});
			viewModel.StatusText = "已在文件资源管理器中打开 " + text;
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "无法打开仓库目录：\n\n" + ex.Message, "打开仓库目录", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void OpenTerminal_OnClick(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(CreateRepositoryTerminalStartInfo(viewModel.ActiveRepositoryPath));
			viewModel.StatusText = "已在终端中打开 " + viewModel.ActiveRepositoryPath;
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "无法打开当前仓库终端：\n\n" + ex.Message, "打开终端", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	internal static ProcessStartInfo CreateRepositoryTerminalStartInfo(string repositoryPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			throw new InvalidOperationException("当前没有已打开的仓库。");
		}

		string fullPath = System.IO.Path.GetFullPath(repositoryPath);
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("当前仓库目录不存在，请重新打开仓库。");
		}

		return new ProcessStartInfo
		{
			FileName = "powershell.exe",
			WorkingDirectory = fullPath,
			UseShellExecute = true
		};
	}

	private async void BranchList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (BranchList.SelectedItem is BranchInfo { IsCurrent: false, IsRemote: false } branchInfo)
		{
			if (string.Equals(rejectedBranchSelectionName, branchInfo.FriendlyName, StringComparison.OrdinalIgnoreCase))
			{
				rejectedBranchSelectionName = null;
				return;
			}
			await viewModel.CheckoutBranchAsync(branchInfo);
		}
	}

	private async void BranchList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 1 && sender is ListBox itemsControl && e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(itemsControl, element) is ListBoxItem { DataContext: BranchInfo dataContext })
		{
			rejectedBranchSelectionName = await viewModel.SelectBranchAsync(dataContext)
				? null
				: dataContext.FriendlyName;
		}
	}

	private void BranchList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is ListBox itemsControl && e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(itemsControl, element) is ListBoxItem listBoxItem)
		{
			listBoxItem.IsSelected = true;
			listBoxItem.Focus();
		}
	}

	private async void DeleteBranch_OnClick(object sender, RoutedEventArgs e)
	{
		object selectedItem = BranchList.SelectedItem;
		if (!(selectedItem is BranchInfo branch))
		{
			return;
		}
		BranchDeletionCheck branchDeletionCheck;
		try
		{
			branchDeletionCheck = await viewModel.CheckBranchDeletionAsync(branch);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "无法检查分支是否可删除：" + ex.Message, "删除分支", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		if (branchDeletionCheck.UncommittedChangeCount > 0)
		{
			MessageBox.Show(this, $"当前工作区还有 {branchDeletionCheck.UncommittedChangeCount} 项已暂存或未暂存的未提交修改。\n\n" + "请先提交或处理这些修改，删除分支操作已中断。", "不能删除分支", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (branchDeletionCheck.IsRemote)
		{
			MessageBox.Show(this, "这里显示的是远程跟踪分支，不能通过本地分支删除功能移除。", "不能删除分支", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (branchDeletionCheck.IsCurrent)
		{
			MessageBox.Show(this, "不能删除当前分支。请先双击切换到其他本地分支，再执行删除。", "不能删除分支", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (branchDeletionCheck.IsMainline)
		{
			MessageBox.Show(this, "不能删除主线分支 " + branchDeletionCheck.MainlineName + "。", "不能删除分支", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		bool flag = !branchDeletionCheck.IsMergedIntoMainline;
		string messageBoxText = (flag ? ($"分支 {branchDeletionCheck.BranchName} 尚未合并到主线 {branchDeletionCheck.MainlineName}。\n\n" + "强制删除可能丢失仅存在于该分支的提交。仍要删除吗？") : $"确定删除已经合并到主线 {branchDeletionCheck.MainlineName} 的分支 {branchDeletionCheck.BranchName} 吗？");
		if (MessageBox.Show(this, messageBoxText, flag ? "分支尚未合并" : "删除分支", MessageBoxButton.YesNo, flag ? MessageBoxImage.Exclamation : MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			await viewModel.DeleteBranchAsync(branch, flag);
		}
	}

	private async void RenameBranch_OnClick(object sender, RoutedEventArgs e)
	{
		if (!(BranchList.SelectedItem is BranchInfo branchInfo))
		{
			MessageBox.Show(this, "请先在左侧选择一个本地分支。", "重命名分支");
			return;
		}
		if (branchInfo.IsRemote)
		{
			MessageBox.Show(this, "远程跟踪分支不能在这里重命名。请先创建或选择本地分支。", "重命名分支", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string text = Prompt("重命名分支", "将分支 " + branchInfo.FriendlyName + " 重命名为：", branchInfo.FriendlyName);
		if (text != null && !string.Equals(text, branchInfo.FriendlyName, StringComparison.Ordinal))
		{
			GitOperationResult gitOperationResult = await viewModel.RenameBranchAsync(branchInfo, text);
			if (!gitOperationResult.Success)
			{
				MessageBox.Show(this, gitOperationResult.ErrorMessage ?? gitOperationResult.Summary, "无法重命名分支", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private async void MergeBranch_OnClick(object sender, RoutedEventArgs e)
	{
		if (BranchList.SelectedItem is BranchInfo branchInfo && MessageBox.Show(this, $"把 {branchInfo.FriendlyName} 合并到 {viewModel.Head?.BranchName ?? viewModel.CurrentBranch}？", "合并预览", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
		{
			await viewModel.MergeBranchAsync(branchInfo);
		}
	}

	private async void FileTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (suppressFileTreeSelection)
		{
			return;
		}
		FileTreeItem? previous = selectedTreeItem;
		FileTreeItem? requested = e.NewValue as FileTreeItem;
		if (await viewModel.SelectFileAsync(requested))
		{
			selectedTreeItem = requested;
			return;
		}

		suppressFileTreeSelection = true;
		try
		{
			TreeViewItem? container = previous == null ? null : FindTreeViewItem(FileTreeView, previous);
			if (container != null)
			{
				container.IsSelected = true;
				container.BringIntoView();
			}
			selectedTreeItem = previous;
		}
		finally
		{
			suppressFileTreeSelection = false;
		}
	}

	private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object item)
	{
		if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
		{
			return direct;
		}
		foreach (object child in parent.Items)
		{
			if (parent.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem treeItem &&
				FindTreeViewItem(treeItem, item) is TreeViewItem nested)
			{
				return nested;
			}
		}
		return null;
	}

	private async void FileTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (selectedTreeItem != null && !selectedTreeItem.IsDirectory)
		{
			await viewModel.SelectFileAsync(selectedTreeItem);
			if ((viewModel.IsExternalOnlyDocument || viewModel.IsExternalDocumentPath(selectedTreeItem.Name)) && await viewModel.OpenFileTreeItemExternallyAsync(selectedTreeItem))
			{
				e.Handled = true;
			}
		}
	}

	private async void CommitGraph_OnCommitSelected(object? sender, CommitSelectedEventArgs e)
	{
		if (!await viewModel.SelectCommitAsync(e.Commit) && sender is CommitGraphControl graph)
		{
			graph.SelectedCommit = viewModel.SelectedCommit;
		}
	}

	private async void CommitGraph_OnBranchSelected(object? sender, BranchSelectedEventArgs e)
	{
		await viewModel.SelectBranchAsync(e.Branch);
	}

	private async void ChangeList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateSelectAllCheckBoxes();
		if (isBulkSelectingFiles || fileSelectionDragging || sender is not ListBox listBox)
		{
			return;
		}
		HashSet<string> accepted = listBox == UnstagedList ? acceptedUnstagedSelection : acceptedStagedSelection;
		if (listBox.SelectedItem is FileChange change && !await viewModel.SelectChangeAsync(change))
		{
			isBulkSelectingFiles = true;
			try
			{
				listBox.SelectedItems.Clear();
				foreach (FileChange item in listBox.Items.Cast<FileChange>().Where(item => accepted.Contains(item.Path)))
				{
					listBox.SelectedItems.Add(item);
				}
			}
			finally
			{
				isBulkSelectingFiles = false;
			}
			return;
		}
		accepted.Clear();
		accepted.UnionWith(listBox.SelectedItems.Cast<FileChange>().Select(item => item.Path));
	}

	private async void ChangeList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not ListBox listBox)
		{
			return;
		}
		fileSelectionList = listBox;
		fileSelectionOverlay = ((listBox == UnstagedList) ? UnstagedSelectionOverlay : StagedSelectionOverlay);
		fileSelectionRectangle = ((listBox == UnstagedList) ? UnstagedSelectionRectangle : StagedSelectionRectangle);
		fileSelectionStart = e.GetPosition(fileSelectionOverlay);
		fileSelectionDragging = false;
		fileSelectionBaseItems = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? listBox.SelectedItems.Cast<object>().ToHashSet() : new HashSet<object>());
		if (e.OriginalSource is DependencyObject element &&
			ItemsControl.ContainerFromElement(listBox, element) is ListBoxItem { DataContext: FileChange dataContext } &&
			listBox.SelectedItems.Contains(dataContext))
		{
			await viewModel.SelectChangeAsync(dataContext);
		}
	}

	private async void DiscardChange_OnClick(object sender, RoutedEventArgs e)
	{
		FileChange[] array = (from FileChange change in ResolveFileOperationTargets(UnstagedList)
			where !change.IsStaged
			select change).DistinctBy<FileChange, string>((FileChange change) => change.Path, StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			MessageBox.Show(this, "请先选择至少一个未暂存修改。", "丢弃修改");
			return;
		}
		int num = array.Count((FileChange change) => change.State == GitChangeState.Untracked);
		string text = string.Join(Environment.NewLine, from change in array.Take(8)
			select "• " + change.Path);
		if (array.Length > 8)
		{
			text += $"{Environment.NewLine}…另有 {array.Length - 8} 个文件";
		}
		string text2 = ((num > 0) ? $"\n\n其中 {num} 个未跟踪文件会被删除。" : string.Empty);
		string text3 = (viewModel.HasUnsavedEditorChanges ? "\n\n注意：编辑器中尚未保存的内容不会写入恢复点；若属于所选文件，将永久丢失。" : string.Empty);
		if (MessageBox.Show(this, $"确定要丢弃所选 {array.Length} 个文件的未暂存修改吗？\n\n{text}" + text2 + text3 + "\n\n程序会先创建自动恢复点，可稍后从恢复中心找回。", "确认丢弃修改", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			GitOperationResult gitOperationResult = await viewModel.DiscardChangesAsync(array);
			if (!gitOperationResult.Success)
			{
				MessageBox.Show(this, gitOperationResult.ErrorMessage ?? gitOperationResult.Summary, "丢弃修改失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private void ChangeList_OnPreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton == MouseButtonState.Pressed && sender is ListBox listBox && listBox == fileSelectionList && fileSelectionOverlay != null && fileSelectionRectangle != null)
		{
			Point position = e.GetPosition(fileSelectionOverlay);
			if (!fileSelectionDragging && (position - fileSelectionStart).Length > 5.0)
			{
				fileSelectionDragging = true;
				listBox.CaptureMouse();
				if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
				{
					listBox.UnselectAll();
				}
				fileSelectionRectangle.Visibility = Visibility.Visible;
			}
			if (fileSelectionDragging)
			{
				UpdateDragSelection(position);
				e.Handled = true;
			}
		}
	}

	private async void StagedList_OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(typeof(FileChange)) is FileChange { IsStaged: false } fileChange)
		{
			await viewModel.StageCommand.ExecuteAsync(fileChange);
		}
	}

	private async void UnstagedList_OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(typeof(FileChange)) is FileChange { IsStaged: not false } fileChange)
		{
			await viewModel.UnstageCommand.ExecuteAsync(fileChange);
		}
	}

	private void Window_OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		e.Handled = true;
	}

	private async void Window_OnDrop(object sender, DragEventArgs e)
	{
		if (!(e.Data.GetData(DataFormats.FileDrop) is string[] array) || array.Length == 0)
		{
			return;
		}
		if (array.Length == 1 && Directory.Exists(array[0]))
		{
			await TryOpenOrInitializeAsync(array[0]);
			return;
		}
		if (!viewModel.HasRepository)
		{
			MessageBox.Show(this, "请先打开一个仓库，再把文件拖入工作区。", "Git 可视化");
			return;
		}
		foreach (string item in array.Where(File.Exists))
		{
			string text = System.IO.Path.Combine(viewModel.ActiveRepositoryPath, System.IO.Path.GetFileName(item));
			if (File.Exists(text))
			{
				MessageBox.Show(this, "目标已存在，未覆盖：\n" + text, "导入文件");
			}
			else
			{
				File.Copy(item, text);
			}
		}
		await viewModel.RefreshAsync();
	}

	private async void CreateBranch_OnClick(object sender, RoutedEventArgs e)
	{
		string text = Prompt("创建分支", "输入新分支名称：");
		if (text != null)
		{
			await viewModel.CreateBranchAsync(text);
		}
	}

	private async void CreateTag_OnClick(object sender, RoutedEventArgs e)
	{
		CommitNode selectedCommit = viewModel.SelectedCommit;
		if ((object)selectedCommit == null)
		{
			MessageBox.Show(DialogOwner, "请先选择一个提交。", "创建 Tag", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}

		CreateTagWindow createTagWindow = new CreateTagWindow(viewModel.History, selectedCommit, viewModel.Tags)
		{
			Owner = DialogOwner
		};
		if (createTagWindow.ShowDialog() != true)
		{
			return;
		}

		GitOperationResult result = await viewModel.CreateTagAsync(
			createTagWindow.TagName,
			createTagWindow.TargetCommitId,
			createTagWindow.TagType,
			createTagWindow.TagMessage);
		ShowOperationFailure(result, "创建 Tag 失败");
	}

	private async void CherryPick_OnClick(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(DialogOwner, "把所选提交应用到当前分支？", "拣选提交", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
		{
			await viewModel.CherryPickSelectedAsync();
		}
	}

	private async void Revert_OnClick(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(DialogOwner, "创建一个新提交来撤销所选提交？", "安全撤销", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
		{
			await viewModel.RevertSelectedAsync();
		}
	}

	private async void Reset_OnClick(object sender, RoutedEventArgs e)
	{
		CommitNode selectedCommit = viewModel.SelectedCommit;
		if ((object)selectedCommit == null)
		{
			MessageBox.Show(DialogOwner, "请先选择一个提交。", "回退当前分支", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		ResetModeWindow resetModeWindow = new ResetModeWindow(viewModel.CurrentBranch, selectedCommit.ShortId, selectedCommit.Message)
		{
			Owner = DialogOwner
		};
		if (resetModeWindow.ShowDialog() == true)
		{
			await viewModel.ResetSelectedAsync(resetModeWindow.SelectedMode);
		}
	}

	private async void ChangeList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!(sender is ListBox listBox) || listBox != fileSelectionList)
		{
			return;
		}
		if (fileSelectionDragging)
		{
			e.Handled = true;
			fileSelectionRectangle.Visibility = Visibility.Collapsed;
			listBox.ReleaseMouseCapture();
		}
		fileSelectionList = null;
		fileSelectionOverlay = null;
		fileSelectionRectangle = null;
		fileSelectionBaseItems = new HashSet<object>();
		fileSelectionDragging = false;
		UpdateSelectAllCheckBoxes();
		if (listBox.SelectedItem is FileChange change)
		{
			await viewModel.SelectChangeAsync(change);
		}
	}

	private void UpdateDragSelection(Point current)
	{
		Canvas canvas = fileSelectionOverlay;
		double num = Math.Clamp(Math.Min(fileSelectionStart.X, current.X), 0.0, canvas.ActualWidth);
		double num2 = Math.Clamp(Math.Min(fileSelectionStart.Y, current.Y), 0.0, canvas.ActualHeight);
		double num3 = Math.Clamp(Math.Max(fileSelectionStart.X, current.X), 0.0, canvas.ActualWidth);
		double num4 = Math.Clamp(Math.Max(fileSelectionStart.Y, current.Y), 0.0, canvas.ActualHeight);
		Rect rect = new Rect(num, num2, num3 - num, num4 - num2);
		Canvas.SetLeft(fileSelectionRectangle, num);
		Canvas.SetTop(fileSelectionRectangle, num2);
		fileSelectionRectangle.Width = rect.Width;
		fileSelectionRectangle.Height = rect.Height;
		isBulkSelectingFiles = true;
		try
		{
			foreach (object item in fileSelectionList.Items.Cast<object>())
			{
				ListBoxItem listBoxItem = fileSelectionList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
				bool flag = false;
				if (listBoxItem != null)
				{
					Point location = listBoxItem.TranslatePoint(new Point(0.0, 0.0), canvas);
					flag = rect.IntersectsWith(new Rect(location, new Size(listBoxItem.ActualWidth, listBoxItem.ActualHeight)));
				}
				bool flag2 = fileSelectionBaseItems.Contains(item) | flag;
				bool flag3 = fileSelectionList.SelectedItems.Contains(item);
				if (flag2 && !flag3)
				{
					fileSelectionList.SelectedItems.Add(item);
				}
				else if (!flag2 & flag3)
				{
					fileSelectionList.SelectedItems.Remove(item);
				}
			}
		}
		finally
		{
			isBulkSelectingFiles = false;
		}
		UpdateSelectAllCheckBoxes();
	}

	private async void SelectAllUnstaged_OnClick(object sender, RoutedEventArgs e)
	{
		await SetAllSelectedAsync(UnstagedList, SelectAllUnstagedCheckBox.IsChecked == true);
	}

	private async void SelectAllStaged_OnClick(object sender, RoutedEventArgs e)
	{
		await SetAllSelectedAsync(StagedList, SelectAllStagedCheckBox.IsChecked == true);
	}

	private async Task SetAllSelectedAsync(ListBox listBox, bool selected)
	{
		isBulkSelectingFiles = true;
		try
		{
			if (selected)
			{
				listBox.SelectAll();
			}
			else
			{
				listBox.UnselectAll();
			}
		}
		finally
		{
			isBulkSelectingFiles = false;
		}
		UpdateSelectAllCheckBoxes();
		if (listBox.SelectedItem is FileChange change)
		{
			await viewModel.SelectChangeAsync(change);
		}
	}

	private void UpdateSelectAllCheckBoxes()
	{
		SelectAllUnstagedCheckBox.IsChecked = UnstagedList.Items.Count > 0 && UnstagedList.SelectedItems.Count == UnstagedList.Items.Count;
		SelectAllStagedCheckBox.IsChecked = StagedList.Items.Count > 0 && StagedList.SelectedItems.Count == StagedList.Items.Count;
	}

	private async void StageSelectedFiles_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.StageSelectedFilesAsync(ResolveFileOperationTargets(UnstagedList));
	}

	private async void UnstageSelectedFiles_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.UnstageSelectedFilesAsync(ResolveFileOperationTargets(StagedList));
	}

	private static FileChange[] ResolveFileOperationTargets(ListBox listBox)
	{
		return ResolveOperationTargets(listBox.SelectedItems.Cast<FileChange>(), listBox.Items.Cast<FileChange>().ToArray());
	}

	internal static T[] ResolveOperationTargets<T>(IEnumerable<T> selectedItems, IReadOnlyCollection<T> availableItems)
	{
		T[] array = selectedItems.ToArray();
		if (array.Length > 0)
		{
			return array;
		}
		return (availableItems.Count == 1) ? new T[1] { availableItems.Single() } : Array.Empty<T>();
	}

	private async void CheckoutCommit_OnClick(object sender, RoutedEventArgs e)
	{
		CommitNode commit = viewModel.SelectedCommit;
		if ((object)commit == null)
		{
			MessageBox.Show(DialogOwner, "请先选择一个提交。", "切换提交");
		}
		else if (MessageBox.Show(DialogOwner, $"切换到提交 {commit.ShortId} 吗？\n\n{commit.Message}\n\n" + "这会进入游离 HEAD 状态，不会移动任何分支。工作区必须没有未提交修改。", "切换到所选提交", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			GitOperationResult gitOperationResult = await viewModel.CheckoutSelectedCommitAsync();
			if (gitOperationResult.Success)
			{
				MessageBox.Show(DialogOwner, "已切换到 " + commit.ShortId + "，当前处于游离 HEAD。\n\n恢复到正常 HEAD：在左侧分支列表中双击任意本地分支即可。\n\n如果你准备在这个版本上继续提交，请先在提交图中选择当前 HEAD，再点击“创建分支”，用新分支保存后续工作。", "已进入游离 HEAD", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
			else
			{
				MessageBox.Show(DialogOwner, gitOperationResult.ErrorMessage ?? gitOperationResult.Summary, "切换提交失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private async void CompareCommits_OnClick(object sender, RoutedEventArgs e)
	{
		CommitNode[] commits = (from @group in viewModel.History.GroupBy<CommitNode, string>((CommitNode commit) => commit.Id, StringComparer.Ordinal)
			select @group.First()).ToArray();
		if (commits.Length < 2)
		{
			MessageBox.Show(DialogOwner, "当前历史列表中至少需要两个提交才能比较。可先切回“全部分支”或加载更多历史。", "比较提交", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		CommitNode preferredNew = viewModel.SelectedCommit ?? commits.FirstOrDefault((CommitNode commit) => string.Equals(commit.Id, viewModel.Head?.CommitId, StringComparison.Ordinal)) ?? commits[0];
		string preferredOldCommitId = preferredNew.ParentIds.FirstOrDefault((string parentId) => commits.Any((CommitNode commit) => string.Equals(commit.Id, parentId, StringComparison.Ordinal))) ?? commits.First((CommitNode commit) => !string.Equals(commit.Id, preferredNew.Id, StringComparison.Ordinal)).Id;
		CommitComparisonWindow commitComparisonWindow = new CommitComparisonWindow(commits, preferredOldCommitId, preferredNew.Id)
		{
			Owner = DialogOwner
		};
		if (commitComparisonWindow.ShowDialog() != true)
		{
			return;
		}
		CommitNode oldCommit = commitComparisonWindow.OldCommit;
		if ((object)oldCommit != null)
		{
			CommitNode newCommit = commitComparisonWindow.NewCommit;
			if ((object)newCommit != null)
			{
				await viewModel.CompareCommitsAsync(oldCommit, newCommit);
			}
		}
	}

	private async void StageSelectedHunks_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.ApplySelectedHunksAsync((from DiffRegionPresentation region in HunkList.SelectedItems
			select region.SourceHunk).OfType<DiffHunk>().ToArray(), unstage: false);
	}

	private async void UnstageSelectedHunks_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.ApplySelectedHunksAsync((from DiffRegionPresentation region in HunkList.SelectedItems
			select region.SourceHunk).OfType<DiffHunk>().ToArray(), unstage: true);
	}

	private void ToggleRawDiff_OnClick(object sender, RoutedEventArgs e)
	{
		viewModel.ToggleRawDiff();
	}

	private async void Pull_OnClick(object sender, RoutedEventArgs e)
	{
		if (!viewModel.HasRepository)
		{
			return;
		}
		PullStrategyWindow dialog = new PullStrategyWindow(viewModel.Remotes, viewModel.Branches, viewModel.SelectedRemote, viewModel.SavedPullStrategy)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		RemoteInfo selectedRemote = dialog.SelectedRemote;
		if ((object)selectedRemote == null || string.IsNullOrWhiteSpace(dialog.SelectedRemoteBranch))
		{
			MessageBox.Show(this, "请选择远程仓库和远程分支。", "拉取");
			return;
		}
		viewModel.SelectedRemote = selectedRemote;
		string remoteName = selectedRemote.Name;
		string branchName = viewModel.Head?.BranchName ?? viewModel.CurrentBranch;
		GitOperationResult gitOperationResult = await viewModel.PullAsync(selectedRemote, dialog.SelectedRemoteBranch, dialog.SelectedStrategy);
		if (gitOperationResult.Success && !viewModel.HasConflicts)
		{
			MessageBox.Show(this, $"远程更新已成功拉取。\n\n远程仓库：{remoteName}\n当前分支：{branchName}\n拉取方式：{PullStrategyDisplayName(dialog.SelectedStrategy)}\n\n结果：{gitOperationResult.Summary}", "拉取完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		else if (!gitOperationResult.Success)
		{
			MessageBox.Show(this, gitOperationResult.ErrorMessage ?? gitOperationResult.Summary, "拉取失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private static string PullStrategyDisplayName(PullStrategy strategy)
	{
		return strategy switch
		{
			PullStrategy.Rebase => "把本地修改接到远程更新之后",
			PullStrategy.FastForwardOnly => "仅在没有分歧时更新",
			_ => "保留双方修改并合并",
		};
	}

	private async void TagManagement_OnClick(object sender, RoutedEventArgs e)
	{
		TagManagementWindow tagManagementWindow = new TagManagementWindow(viewModel.Tags)
		{
			Owner = this
		};
		if (tagManagementWindow.ShowDialog() == true)
		{
			GitOperationResult gitOperationResult = ((tagManagementWindow.Action != TagManagementAction.Create) ? (await viewModel.DeleteTagAsync(tagManagementWindow.TagName)) : (await viewModel.CreateTagAsync(tagManagementWindow.TagName, viewModel.SelectedCommit?.Id)));
			GitOperationResult result = gitOperationResult;
			ShowOperationFailure(result, "标签操作失败");
		}
	}

	private async void StashManagement_OnClick(object sender, RoutedEventArgs e)
	{
		IReadOnlyList<StashInfo> stashes;
		try
		{
			stashes = await viewModel.GetStashesAsync();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, "无法读取临时现场", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		StashManagementWindow stashManagementWindow = new StashManagementWindow(stashes)
		{
			Owner = this
		};
		if (stashManagementWindow.ShowDialog() == true)
		{
			GitOperationResult gitOperationResult = stashManagementWindow.Action switch
			{
				StashManagementAction.Save => await viewModel.SaveStashAsync(stashManagementWindow.StashMessage),
				StashManagementAction.Apply => await viewModel.ApplyStashAsync(stashManagementWindow.SelectedIndex, pop: false),
				StashManagementAction.Pop => await viewModel.ApplyStashAsync(stashManagementWindow.SelectedIndex, pop: true),
				StashManagementAction.Delete => await viewModel.DeleteStashAsync(stashManagementWindow.SelectedIndex),
				_ => null,
			};
			if ((object)gitOperationResult != null)
			{
				ShowOperationFailure(gitOperationResult, "临时现场操作失败");
			}
		}
	}

	private async void Rebase_OnClick(object sender, RoutedEventArgs e)
	{
		if (viewModel.Head?.IsDetached ?? false)
		{
			MessageBox.Show(this, "请先切换到本地分支，再执行变基。", "变基");
			return;
		}
		RebaseWindow rebaseWindow = new RebaseWindow((from branch in viewModel.Branches
			where !branch.IsCurrent
			select branch.FriendlyName).ToArray())
		{
			Owner = this
		};
		if (rebaseWindow.ShowDialog() == true)
		{
			ShowOperationFailure(await viewModel.RebaseOntoAsync(rebaseWindow.UpstreamBranch, rebaseWindow.OntoBranch), "变基失败");
		}
	}

	private async void ForcePush_OnClick(object sender, RoutedEventArgs e)
	{
		RemoteInfo selectedRemote = viewModel.SelectedRemote;
		string text = viewModel.Head?.BranchName;
		if ((object)selectedRemote == null || string.IsNullOrWhiteSpace(text))
		{
			MessageBox.Show(this, "请先选择远程仓库，并确保当前位于本地分支。", "Force-with-lease 推送");
		}
		else if (new ForcePushConfirmationWindow(selectedRemote.Name, text)
		{
			Owner = this
		}.ShowDialog() == true)
		{
			await RunPushAsync(selectedRemote, forceWithLease: true);
		}
	}

	private void ShowOperationFailure(GitOperationResult result, string title)
	{
		if (!result.Success)
		{
			MessageBox.Show(DialogOwner, result.ErrorMessage ?? result.Summary, title, MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async void Identity_OnClick(object sender, RoutedEventArgs e)
	{
		if (!viewModel.HasRepository)
		{
			MessageBox.Show(this, "请先打开一个仓库。", "Git 身份");
			return;
		}
		GitIdentity? currentIdentity = await viewModel.GetCurrentIdentityAsync();
		string text = Prompt("Git 身份", "用户名：", currentIdentity?.Name ?? string.Empty);
		if (text == null)
		{
			return;
		}
		string text2 = Prompt("Git 身份", "邮箱：", currentIdentity?.Email ?? string.Empty);
		if (text2 != null)
		{
			MessageBoxResult messageBoxResult = MessageBox.Show(this, "选择“是”设置为所有仓库的默认身份；选择“否”只修改当前仓库。", "配置范围", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (messageBoxResult != MessageBoxResult.Cancel)
			{
				await viewModel.ConfigureIdentityAsync(new GitIdentity(text, text2), messageBoxResult == MessageBoxResult.Yes);
			}
		}
	}

	private async void Credential_OnClick(object sender, RoutedEventArgs e)
	{
		if (viewModel.Remotes.Count == 0)
		{
			MessageBox.Show(this, "当前仓库没有远程地址。", "远程凭据");
			return;
		}
		RemoteInfo remote = viewModel.SelectedRemote ?? viewModel.Remotes[0];
		string fetchUrl = remote.FetchUrl;
		if (fetchUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || (fetchUrl.Contains('@') && fetchUrl.Contains(':')))
		{
			await viewModel.SaveRemoteCredentialAsync(remote, new RemoteCredential(CredentialKind.SshAgent));
			MessageBox.Show(this, "此远程将使用 Windows SSH Agent。请先把私钥加入系统 SSH Agent；应用不会读取或记录私钥内容。", "SSH 凭据", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		RemoteCredential savedCredential = await viewModel.LoadSavedRemoteCredentialAsync(remote);
		CredentialWindow credentialWindow = new CredentialWindow(remote.FetchUrl, savedCredential)
		{
			Owner = this
		};
		if (credentialWindow.ShowDialog() == true)
		{
			if (credentialWindow.DeleteRequested)
			{
				await viewModel.DeleteRemoteCredentialAsync(remote);
			}
			else
			{
				await viewModel.SaveRemoteCredentialAsync(remote, credentialWindow.Credential);
			}
		}
	}

	private async void NewFile_OnClick(object sender, RoutedEventArgs e)
	{
		await CreateFileSystemItemAsync(directory: false, "新建 文本文档.txt");
	}

	private async void NewFolder_OnClick(object sender, RoutedEventArgs e)
	{
		await CreateFileSystemItemAsync(directory: true, "新建文件夹");
	}

	private void BranchActionsMenu_OnClick(object sender, RoutedEventArgs e)
	{
		if (viewModel.HasRepository && sender is Button { ContextMenu: not null } button)
		{
			button.ContextMenu.PlacementTarget = button;
			button.ContextMenu.IsOpen = true;
		}
	}

	private async void NewItemMenu_OnClick(object sender, RoutedEventArgs e)
	{
		if (viewModel.HasRepository && sender is Button { ContextMenu: not null } button)
		{
			await PopulateSystemNewTypesAsync();
			button.ContextMenu.PlacementTarget = button;
			button.ContextMenu.IsOpen = true;
		}
	}

	private async Task PopulateSystemNewTypesAsync()
	{
		SystemNewTypesMenuItem.Items.Clear();
		SystemNewTypesMenuItem.Items.Add(new MenuItem
		{
			Header = "正在读取系统新建类型…",
			IsEnabled = false
		});
		try
		{
			SystemNewFileType[] array = (await viewModel.GetSystemNewFileTypesAsync()).Where((SystemNewFileType type) => !BuiltInNewFileExtensions.Contains(type.Extension)).ToArray();
			SystemNewTypesMenuItem.Items.Clear();
			SystemNewFileType[] array2 = array;
			foreach (SystemNewFileType systemNewFileType in array2)
			{
				MenuItem menuItem = new MenuItem
				{
					Header = systemNewFileType.DisplayName + " (" + systemNewFileType.Extension + ")",
					Tag = systemNewFileType,
					Icon = CreateFileTypeIcon(systemNewFileType.Extension)
				};
				menuItem.Click += SystemNewItemType_OnClick;
				SystemNewTypesMenuItem.Items.Add(menuItem);
			}
			if (array.Length == 0)
			{
				SystemNewTypesMenuItem.Items.Add(new MenuItem
				{
					Header = "未发现其他安全的系统模板",
					IsEnabled = false
				});
			}
		}
		catch (Exception ex)
		{
			SystemNewTypesMenuItem.Items.Clear();
			SystemNewTypesMenuItem.Items.Add(new MenuItem
			{
				Header = "读取失败：" + ex.Message,
				IsEnabled = false
			});
		}
	}

	private async void NewItemType_OnClick(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Tag: string tag })
		{
			var (directory, suggestedName) = tag switch
			{
				"folder" => (true, "新建文件夹"),
				".md" => (false, "README.md"),
				".docx" => (false, "新建 Word 文档.docx"),
				".cs" => (false, "新建类.cs"),
				".json" => (false, "data.json"),
				".xml" => (false, "data.xml"),
				_ => (false, "新建 文本文档.txt"),
			};
			await CreateFileSystemItemAsync(directory, suggestedName, (tag == "folder") ? null : tag);
		}
	}

	private async void SystemNewItemType_OnClick(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Tag: SystemNewFileType tag })
		{
			await CreateFileSystemItemAsync(directory: false, tag.SuggestedFileName, tag.Extension, tag);
		}
	}

	private async Task CreateFileSystemItemAsync(bool directory, string suggestedName, string? requiredExtension = null, SystemNewFileType? systemType = null)
	{
		if (!viewModel.HasRepository)
		{
			return;
		}
		string text = ((selectedTreeItem == null) ? viewModel.ActiveRepositoryPath : (selectedTreeItem.IsDirectory ? selectedTreeItem.FullPath : (System.IO.Path.GetDirectoryName(selectedTreeItem.FullPath) ?? viewModel.ActiveRepositoryPath)));
		string text2 = Prompt(directory ? "新建文件夹" : "新建文件", "创建位置：\n" + text + "\n\n名称：", suggestedName);
		if (string.IsNullOrWhiteSpace(text2))
		{
			return;
		}
		text2 = text2.Trim();
		bool flag = ((text2 == "." || text2 == "..") ? true : false);
		if (flag || !System.IO.Path.GetFileName(text2).Equals(text2, StringComparison.Ordinal) || text2.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
		{
			MessageBox.Show(this, "请输入不包含路径分隔符的有效名称。", "名称无效", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (requiredExtension != null)
		{
			string extension = System.IO.Path.GetExtension(text2);
			if (string.IsNullOrEmpty(extension))
			{
				text2 += requiredExtension;
			}
			else if ((object)systemType != null && !extension.Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
			{
				MessageBox.Show(this, "该模板要求文件名使用 " + requiredExtension + " 扩展名。", "扩展名不匹配", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
		}
		try
		{
			if ((object)systemType == null)
			{
				await viewModel.CreateFileAsync(text, text2, directory);
			}
			else
			{
				await viewModel.CreateSystemFileAsync(text, text2, systemType);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, directory ? "无法创建文件夹" : "无法创建文件", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async void RenameFile_OnClick(object sender, RoutedEventArgs e)
	{
		if (selectedTreeItem != null)
		{
			string text = Prompt("重命名", "新名称：", selectedTreeItem.Name);
			if (text != null && !text.Equals(selectedTreeItem.Name, StringComparison.Ordinal))
			{
				await viewModel.MoveFileAsync(selectedTreeItem.FullPath, text);
			}
		}
	}

	private async void DeleteFile_OnClick(object sender, RoutedEventArgs e)
	{
		if (selectedTreeItem != null && MessageBox.Show(this, "删除以下项目？Git 会把删除记录为工作区变化。\n\n" + selectedTreeItem.FullPath, "删除文件", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			await viewModel.DeleteFileAsync(selectedTreeItem.FullPath);
		}
	}

	private void ConflictList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		ListBox list = (ListBox)sender;
		ConflictFile? conflict = list.SelectedItem as ConflictFile;
		if (conflict == null && viewModel.Conflicts.Count > 0)
		{
			int tabIndex = viewModel.SelectedRightTabIndex;
			string? selectedPath = viewModel.SelectedConflict?.Path;
			string conflictResultText = viewModel.ConflictResultText;
			conflict = ResolveConflictSelection(viewModel.SelectedConflict?.Path, viewModel.Conflicts);
			viewModel.SelectConflict(conflict);
			if (conflict != null &&
				conflict.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
			{
				viewModel.ConflictResultText = conflictResultText;
			}
			viewModel.SelectedRightTabIndex = tabIndex;
			list.SelectedItem = conflict;
			return;
		}
		if (conflict != null &&
			conflict.Path.Equals(viewModel.SelectedConflict?.Path, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		viewModel.SelectConflict(conflict);
	}

	private void UseOurs_OnClick(object sender, RoutedEventArgs e)
	{
		viewModel.UseConflictSide(ConflictSide.Ours);
	}

	private void UseTheirs_OnClick(object sender, RoutedEventArgs e)
	{
		viewModel.UseConflictSide(ConflictSide.Theirs);
	}

	private void UseBoth_OnClick(object sender, RoutedEventArgs e)
	{
		viewModel.UseConflictSide(ConflictSide.Both);
	}

	private async void ResolveConflict_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.ResolveSelectedConflictAsync();
	}

	private async void ResolveBinaryOurs_OnClick(object sender, RoutedEventArgs e)
	{
		await ResolveBinaryConflictAsync(ConflictSide.Ours);
	}

	private async void ResolveBinaryTheirs_OnClick(object sender, RoutedEventArgs e)
	{
		await ResolveBinaryConflictAsync(ConflictSide.Theirs);
	}

	private async void ResolveBinaryCurrentFile_OnClick(object sender, RoutedEventArgs e)
	{
		await ResolveBinaryConflictAsync(ConflictSide.CurrentFile);
	}

	private async Task ResolveBinaryConflictAsync(ConflictSide side)
	{
		if (MessageBox.Show(DialogOwner, "采用" + side switch
		{
			ConflictSide.Ours => "当前版本（ours）",
			ConflictSide.Theirs => "对方版本（theirs）",
			_ => "当前工作区文件",
		} + "的原始字节并标记该冲突为已解决？", "解决二进制冲突", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			await viewModel.ResolveSelectedBinaryConflictAsync(side);
		}
	}

	private async void ContinueOperation_OnClick(object sender, RoutedEventArgs e)
	{
		await viewModel.ContinueOperationAsync();
	}

	private async void AbortOperation_OnClick(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(DialogOwner, "中止当前 Git 操作并恢复到操作前状态？", "中止操作", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			await viewModel.AbortOperationAsync();
		}
	}

	private async void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			await viewModel.SaveEditorCommand.ExecuteAsync(null);
			e.Handled = true;
		}
		else if (e.Key == Key.F5)
		{
			await viewModel.RefreshAsync();
			e.Handled = true;
		}
	}

	private string? Prompt(string title, string text, string initialValue = "")
	{
		TextPromptWindow textPromptWindow = new TextPromptWindow(title, text, initialValue)
		{
			Owner = DialogOwner
		};
		if (textPromptWindow.ShowDialog() != true)
		{
			return null;
		}
		return textPromptWindow.Value;
	}

	private static string GuessRepositoryName(string url)
	{
		string text = url.TrimEnd(new char[2] { '/', '\\' });
		int num = Math.Max(text.LastIndexOf('/'), text.LastIndexOf(':'));
		string text2 = ((num >= 0) ? text.Substring(num + 1) : text);
		if (!text2.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			return text2;
		}
		string text3 = text2;
		return text3.Substring(0, text3.Length - 4);
	}

	private static bool PathsEqual(string left, string right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
		{
			return false;
		}
		return System.IO.Path.GetFullPath(left).Equals(System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
	}

	private static Image? CreateFileTypeIcon(string extension)
	{
		ShellFileInfo shellFileInfo = default(ShellFileInfo);
		if (SHGetFileInfo("file" + extension, 128u, ref shellFileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), 273u) == IntPtr.Zero || shellFileInfo.IconHandle == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(shellFileInfo.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			bitmapSource.Freeze();
			return new Image
			{
				Source = bitmapSource,
				Width = 16.0,
				Height = 16.0
			};
		}
		finally
		{
			DestroyIcon(shellFileInfo.IconHandle);
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo shellFileInfo, uint shellFileInfoSize, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(nint iconHandle);

}
