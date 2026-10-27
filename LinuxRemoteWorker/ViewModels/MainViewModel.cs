using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.AppLogs;
using LinuxRemoteWorker.Modules.Firewall;
using LinuxRemoteWorker.Modules.Logs;
using LinuxRemoteWorker.Modules.Nginx;
using LinuxRemoteWorker.Modules.Postgres;
using LinuxRemoteWorker.Modules.Repositories;
using LinuxRemoteWorker.Modules.Security;
using LinuxRemoteWorker.Modules.Services;
using LinuxRemoteWorker.Modules.SystemInfo;
using LinuxRemoteWorker.Update;

namespace LinuxRemoteWorker.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    private readonly SshService _ssh;

    public ConnectViewModel ConnectVM { get; }
    public SystemInfoViewModel SystemInfoVM { get; }
    public PostgresViewModel PostgresVM { get; }
    public FirewallViewModel FirewallVM { get; }
    public SecurityViewModel SecurityVM { get; }
    public RepositoriesViewModel RepositoriesVM { get; }
    public ServicesViewModel ServicesVM { get; }
    public NginxViewModel NginxVM { get; }
    public LogsViewModel LogsVM { get; }
    public AppLogViewModel AppLogVM { get; }

    /// <summary>Version and automatic updates, shown at the bottom of the sidebar.</summary>
    public UpdateService Updates { get; }

    [ObservableProperty] private BaseViewModel? _activeModule;
    [ObservableProperty] private bool _isConnected;

    public MainViewModel()
    {
        _ssh = new SshService();

        ConnectVM = new ConnectViewModel(_ssh);
        SystemInfoVM = new SystemInfoViewModel(_ssh);
        PostgresVM = new PostgresViewModel(_ssh);
        FirewallVM = new FirewallViewModel(_ssh);
        SecurityVM = new SecurityViewModel(_ssh, (keyPath, user, passphrase) => ConnectVM.SwitchToKeyAsync(keyPath, user, passphrase));
        RepositoriesVM = new RepositoriesViewModel(_ssh);
        ServicesVM = new ServicesViewModel(_ssh);
        NginxVM = new NginxViewModel(_ssh);
        LogsVM = new LogsViewModel(_ssh);
        AppLogVM = new AppLogViewModel();

        Updates = new UpdateService(UpdateService.IsDisabled(Environment.GetCommandLineArgs()));
        Updates.Start();

        ConnectVM.ConnectedSuccessfully += OnConnected;
        // Disconnecting on the connection screen must also lock the module navigation again
        ConnectVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectViewModel.IsConnected) && !ConnectVM.IsConnected)
                IsConnected = false;
        };
    }

    private async void OnConnected()
    {
        IsConnected = true;
        await NavigateToAsync(SystemInfoVM);
    }

    [RelayCommand]
    private async Task NavigateToAsync(BaseViewModel module)
    {
        ActiveModule = module;

        // The app-log viewer works without a server, so it refreshes itself instead.
        if (module is AppLogViewModel appLog)
        {
            appLog.Reload();
            return;
        }

        if (module is IModule m)
            await m.LoadAsync(_ssh);
    }
}
