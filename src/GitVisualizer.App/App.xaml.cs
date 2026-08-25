using System.Windows;
using GitVisualizer.App.ViewModels;
using GitVisualizer.Core;
using GitVisualizer.Infrastructure.FileSystem;
using GitVisualizer.Infrastructure.Git;
using GitVisualizer.Infrastructure.Diagnostics;
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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DiagnosticLog.Write("AppDomain", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLog.Write("Task", args.Exception);
            args.SetObserved();
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IOperationLogStore, OperationLogStore>();
        services.AddSingleton<IRecoveryService, RecoveryService>();
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

        var window = serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        await serviceProvider.GetRequiredService<MainWindowViewModel>().InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
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
                // The error dialog must still remain usable if writing the diagnostic log fails.
            }

            var message =
                "应用遇到未处理错误，诊断信息已保存在本机日志目录。\n\n" +
                args.Exception.Message;
            if (MainWindow is { } owner)
            {
                MessageBox.Show(
                    owner,
                    message,
                    "Git 可视化",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(
                    message,
                    "Git 可视化",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            Interlocked.Exchange(ref unhandledErrorDialogVisible, 0);
        }
    }
}
