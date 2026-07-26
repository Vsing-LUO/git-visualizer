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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLog.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Write("Dispatcher", args.Exception);
            MessageBox.Show(
                "应用遇到未处理错误，诊断信息已保存在本机日志目录。\n\n" + args.Exception.Message,
                "Git 可视化",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
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
}
