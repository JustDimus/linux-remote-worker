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

        AppLog.Info($"==== App started (log: {AppLog.CurrentFile}) ====");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled domain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"Unexpected error:\n{e.Exception.Message}\n\nLogged to:\n{AppLog.CurrentFile}",
            "Linux Remote Worker", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the app alive
    }
}
