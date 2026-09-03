using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GitVisualizer.App.Services;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.Diagnostics;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Persistence;
using GitVisualizer.Infrastructure.Recovery;
using GitVisualizer.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace GitVisualizer.App;

public partial class App : Application
{
	private ServiceProvider? serviceProvider;

	private int unhandledErrorDialogVisible;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		DiagnosticLog.Initialize();
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			if (args.ExceptionObject is Exception exception)
			{
				DiagnosticLog.Write("AppDomain", exception);
			}
		};
		TaskScheduler.UnobservedTaskException += delegate(object? _, UnobservedTaskExceptionEventArgs args)
		{
			DiagnosticLog.Write("Task", args.Exception);
			args.SetObserved();
		};
		ServiceCollection services = new ServiceCollection();
		services.AddSingleton<ISettingsStore, SettingsStore>();
		services.AddSingleton<IOperationLogStore, OperationLogStore>();
		services.AddSingleton<IRecoveryService, RecoveryService>();
		services.AddSingleton<IEditorDraftStore, EditorDraftStore>();
		services.AddSingleton<IEditorInteractionService, EditorInteractionService>();
		services.AddSingleton<ICredentialVault, WindowsCredentialVault>();
		services.AddSingleton<IRepositoryWatcherFactory, RepositoryWatcherFactory>();
		services.AddSingleton<IFileWorkspaceService, FileWorkspaceService>();
		services.AddSingleton<ISystemNewFileService, WindowsShellNewFileService>();
		services.AddSingleton<IDiffService, LibGitDiffService>();
		services.AddSingleton<IIndexPatchService, LibGitIndexPatchService>();
		services.AddSingleton<IGitRepositoryService, LibGitRepositoryService>();
		services.AddSingleton<MainWindowViewModel>();
		services.AddSingleton<MainWindow>();
		serviceProvider = services.BuildServiceProvider();
		await serviceProvider.GetRequiredService<IOperationLogStore>().InitializeAsync();
		MainWindow mainWindow = (MainWindow)(base.MainWindow = serviceProvider.GetRequiredService<MainWindow>());
		mainWindow.Show();
		await serviceProvider.GetRequiredService<MainWindowViewModel>().InitializeAsync();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		serviceProvider?.Dispose();
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
	{
		args.Handled = true;
		if (Interlocked.Exchange(ref unhandledErrorDialogVisible, 1) != 0)
		{
			return;
		}
		try
		{
			try
			{
				DiagnosticLog.Write("Dispatcher", args.Exception);
			}
			catch
			{
			}
			string messageBoxText = "应用遇到未处理错误，诊断信息已保存在本机日志目录。\n\n" + args.Exception.Message;
			Window mainWindow = base.MainWindow;
			if (mainWindow != null)
			{
				MessageBox.Show(mainWindow, messageBoxText, "Git 可视化", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			else
			{
				MessageBox.Show(messageBoxText, "Git 可视化", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
		finally
		{
			Interlocked.Exchange(ref unhandledErrorDialogVisible, 0);
		}
	}
}
