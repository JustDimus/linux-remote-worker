using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Firewall;
using LinuxRemoteWorker.Modules.Postgres;
using LinuxRemoteWorker.Modules.Repositories;
using LinuxRemoteWorker.Modules.Services;
using LinuxRemoteWorker.Modules.SystemInfo;

namespace LinuxRemoteWorker.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    private readonly SshService _ssh;

    public ConnectViewModel ConnectVM { get; }
    public SystemInfoViewModel SystemInfoVM { get; }
    public PostgresViewModel PostgresVM { get; }
    public FirewallViewModel FirewallVM { get; }
    public RepositoriesViewModel RepositoriesVM { get; }
    public ServicesViewModel ServicesVM { get; }

    [ObservableProperty] private BaseViewModel? _activeModule;
    [ObservableProperty] private bool _isConnected;

    public MainViewModel()
    {
        _ssh = new SshService();

        ConnectVM = new ConnectViewModel(_ssh);
        SystemInfoVM = new SystemInfoViewModel(_ssh);
        PostgresVM = new PostgresViewModel(_ssh);
        FirewallVM = new FirewallViewModel(_ssh);
        RepositoriesVM = new RepositoriesViewModel(_ssh);
        ServicesVM = new ServicesViewModel(_ssh);

        ConnectVM.ConnectedSuccessfully += OnConnected;
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
        if (module is IModule m)
            await m.LoadAsync(_ssh);
    }
}
