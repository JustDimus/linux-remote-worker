using System.Windows;
using System.Windows.Threading;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.CleanupOldFiles();

        AppLog.Info("======================================================");
        AppLog.Info($"App started - v{typeof(App).Assembly.GetName().Version}");
        AppLog.Info($"Log file: {AppLog.CurrentFile}");
        AppLog.Info($"Profiles: {ProfileService.ProfilesPath}");
        AppLog.Info($"OS: {Environment.OSVersion} | 64-bit: {Environment.Is64BitOperatingSystem} | user: {Environment.UserName}");
        AppLog.Info($"Runtime: {Environment.Version} | machine: {Environment.MachineName}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled domain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"App exiting with code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"Unexpected error:\n{e.Exception.Message}\n\nLogged to:\n{AppLog.CurrentFile}",
            "Linux Remote Worker", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the app alive
    }
}
