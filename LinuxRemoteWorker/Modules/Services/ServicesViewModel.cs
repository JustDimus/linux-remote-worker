using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Logs;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Services;

public partial class ServicesViewModel : BaseViewModel, IModule
{
    public string Title => "Services";
    public string Icon => "⚙";

    private const int JournalLines = 500;

    private readonly BootstrapService _bootstrap;
    private readonly DotnetSdkService _dotnet;
    private readonly AppServiceManager _services;
    private readonly ServerLogService _logs;
    private CancellationTokenSource? _logCts;

    [ObservableProperty] private bool _isBusyServices;
    [ObservableProperty] private string _outputLog = string.Empty;

    // .NET SDK tooling
    [ObservableProperty] private bool _dotnetInstalled;
    [ObservableProperty] private string _dotnetVersion = string.Empty;
    [ObservableProperty] private string _installedSdks = string.Empty;
    [ObservableProperty] private string _installedRuntimes = string.Empty;
    [ObservableProperty] private string _selectedSdkVersion = "10.0";
    public IReadOnlyList<string> SdkVersionOptions => DotnetSdkService.KnownChannels;

    // Deploy form
    public ObservableCollection<string> AvailableRepos { get; } = [];
    public ObservableCollection<string> CsprojFiles { get; } = [];
    [ObservableProperty] private string? _selectedRepo;
    [ObservableProperty] private string? _selectedCsproj;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _aspNetUrls = "http://0.0.0.0:5000";

    // Services
    public ObservableCollection<ServiceInfo> Services { get; } = [];

    // Selected service detail
    [ObservableProperty] private ServiceInfo? _selectedService;
    [ObservableProperty] private string _unitContent = string.Empty;
    [ObservableProperty] private string _logOutput = string.Empty;
    [ObservableProperty] private bool _isStreamingLogs;

    public ServicesViewModel(SshService ssh)
    {
        _bootstrap = new BootstrapService(ssh);
        _dotnet = new DotnetSdkService(ssh);
        _services = new AppServiceManager(ssh);
        _logs = new ServerLogService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await _bootstrap.EnsureAsync();

            var dotnet = await _dotnet.GetStatusAsync();
            DotnetInstalled = dotnet.Installed;
            DotnetVersion = dotnet.Installed ? $"default SDK {dotnet.DefaultSdk}" : string.Empty;
            InstalledSdks = string.Join("\n", dotnet.Sdks);
            InstalledRuntimes = string.Join("\n", dotnet.Runtimes);

            await LoadReposAsync();
            await LoadServicesAsync();
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task InstallDotnetAsync()
    {
        var version = SelectedSdkVersion;
        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            Append($"→ Installing .NET SDK {version} (includes .NET + ASP.NET Core runtime)...");
            await _dotnet.InstallAsync(version, Append);
            Append("\n✓ Done");
            await RefreshAsync();
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task UninstallDotnetAsync()
    {
        var version = SelectedSdkVersion;
        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            Append($"→ Removing .NET {version} (sdk + runtimes) from {DotnetSdkService.Root}...");
            await _dotnet.UninstallAsync(version, Append);
            Append("\n✓ Done");
            await RefreshAsync();
        });
        IsBusyServices = false;
    }

    private async Task LoadReposAsync()
    {
        var repos = await _services.ListReposAsync();
        AvailableRepos.Clear();
        foreach (var repo in repos) AvailableRepos.Add(repo);
    }

    private async Task LoadServicesAsync()
    {
        var services = await _services.ListAsync();
        Services.Clear();
        foreach (var service in services) Services.Add(service);
    }

    partial void OnSelectedRepoChanged(string? value)
    {
        _ = RunSafeAsync(() => LoadCsprojAsync(value));
    }

    private async Task LoadCsprojAsync(string? repo)
    {
        CsprojFiles.Clear();
        if (string.IsNullOrWhiteSpace(repo)) return;

        foreach (var f in await _services.FindProjectsAsync(repo))
            CsprojFiles.Add(f);
    }

    partial void OnSelectedCsprojChanged(string? value)
    {
        // Suggest app name from csproj file name
        if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(AppName))
            AppName = AppServiceManager.SuggestAppName(value);
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        var csproj = SelectedCsproj;
        if (string.IsNullOrWhiteSpace(csproj) || string.IsNullOrWhiteSpace(AppName))
        {
            SetStatus("Select a .csproj and enter an app name", isError: true);
            return;
        }

        var error = AppServiceManager.ValidateNewAppName(AppName.Trim()) ?? AppServiceManager.ValidateUrls(AspNetUrls.Trim());
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var app = AppName.Trim();
            await _services.DeployAsync(csproj, app, AspNetUrls, Append);

            Append("\n✓ Deployed and started");
            await LoadServicesAsync();
            SetStatus($"Service {app} deployed");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task RedeployAsync(ServiceInfo s)
    {
        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            await _services.RedeployAsync(s.AppName, pull: false, Append);

            Append("\n✓ Redeployed");
            await LoadServicesAsync();
            SetStatus($"{s.AppName} redeployed from latest");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task StartAsync(ServiceInfo s) => await ServiceActionAsync(s, ServiceAction.Start);

    [RelayCommand]
    private async Task StopAsync(ServiceInfo s) => await ServiceActionAsync(s, ServiceAction.Stop);

    [RelayCommand]
    private async Task RestartAsync(ServiceInfo s) => await ServiceActionAsync(s, ServiceAction.Restart);

    private async Task ServiceActionAsync(ServiceInfo s, ServiceAction action)
    {
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await ThenReloadAsync(() => _services.ControlAsync(s.AppName, action), LoadServicesAsync);
            SetStatus($"{s.AppName}: {action.ToString().ToLowerInvariant()} done");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task DeleteServiceAsync(ServiceInfo s)
    {
        if (!Confirm($"Remove service {s.UnitName}?\n\nIt is stopped and its unit file deleted. " +
                            $"The published app stays in {DeployPaths.AppDir(s.AppName)}.", "Remove service"))
            return;

        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await _services.RemoveAsync(s.AppName);
            if (SelectedService?.UnitName == s.UnitName)
                SelectedService = null;
            await LoadServicesAsync();
            SetStatus($"Service {s.AppName} removed (app files kept in {DeployPaths.AppDir(s.AppName)})");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task SelectServiceAsync(ServiceInfo s)
    {
        SelectedService = s;
        LogOutput = string.Empty;
        UnitContent = string.Empty; // never show the previous service's unit under this one's name
        await RunSafeAsync(async () =>
        {
            UnitContent = await _services.ReadUnitAsync(s.AppName);
        });
    }

    [RelayCommand]
    private async Task SaveUnitAsync()
    {
        if (SelectedService == null) return;
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            var app = SelectedService.AppName;
            await ThenReloadAsync(() => _services.SaveUnitAsync(app, UnitContent), LoadServicesAsync);
            SetStatus("Unit saved, daemon reloaded, service restarted");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task LogsHourAsync() => await LoadLogsAsync("1 hour ago");

    [RelayCommand]
    private async Task LogsDayAsync() => await LoadLogsAsync("1 day ago");

    private async Task LoadLogsAsync(string since)
    {
        if (SelectedService == null) return;
        await RunSafeAsync(async () =>
        {
            LogOutput = await _logs.JournalAsync(SelectedService.UnitName, since, JournalLines);
            if (string.IsNullOrWhiteSpace(LogOutput))
                LogOutput = "(no log entries)";
        });
    }

    [RelayCommand]
    private async Task LogsLiveAsync()
    {
        if (SelectedService == null || IsStreamingLogs) return;

        _logCts = new CancellationTokenSource();
        IsStreamingLogs = true;
        LogOutput = string.Empty;

        try
        {
            await _logs.FollowJournalAsync(SelectedService.UnitName, 50, line => LogOutput += line + "\n", _logCts.Token);
        }
        catch (OperationCanceledException) { /* stopped by user */ }
        catch (Exception ex) { SetStatus(ex.Message, isError: true); }
        finally { IsStreamingLogs = false; }
    }

    [RelayCommand]
    private void StopLogs()
    {
        _logCts?.Cancel();
        IsStreamingLogs = false;
    }
}
