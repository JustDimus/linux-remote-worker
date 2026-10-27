using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Logs;
using LinuxRemoteWorker.Modules.Repositories;
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
    private readonly RepositoryService _repos;
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
    [ObservableProperty] private string _deployEnvironment = AppServiceManager.DefaultEnvironment;

    // Services
    public ObservableCollection<ServiceInfo> Services { get; } = [];

    // Selected service detail
    [ObservableProperty] private ServiceInfo? _selectedService;
    [ObservableProperty] private string _unitContent = string.Empty;
    [ObservableProperty] private string _logOutput = string.Empty;
    [ObservableProperty] private bool _isStreamingLogs;

    // Version of the selected service: what to move its clone to before rebuilding
    public GitSourcePickerViewModel VersionPicker { get; } = new(allowDefaultBranch: false);

    public ServicesViewModel(SshService ssh)
    {
        _bootstrap = new BootstrapService(ssh);
        _dotnet = new DotnetSdkService(ssh);
        _services = new AppServiceManager(ssh);
        _repos = new RepositoryService(ssh);
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

        // Keep the managed service's version line current after a redeploy
        if (SelectedService != null)
            SelectedService = services.FirstOrDefault(s => s.UnitName == SelectedService.UnitName);
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

        var error = AppServiceManager.ValidateNewAppName(AppName.Trim()) ?? AppServiceManager.ValidateUrls(AspNetUrls.Trim())
                    ?? AppServiceManager.ValidateEnvironment(DeployEnvironment.Trim());
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
            DeploymentInfo deployment;
            try
            {
                deployment = await _services.DeployAsync(csproj, app, AspNetUrls, Append, DeployEnvironment);
            }
            catch (OperationException ex) when (ex.Kind == FailureKind.Refused)
            {
                // A port another service is set up for, or that something already listens on
                if (!Confirm($"{ex.Message}\n\nDeploy anyway? The service may fail to start.", "Port in use"))
                    throw;
                deployment = await _services.DeployAsync(csproj, app, AspNetUrls, Append, DeployEnvironment, skipPortCheck: true);
            }

            Append("\n✓ Deployed and started");
            await LoadServicesAsync();
            SetStatus($"Service {app} deployed: {deployment.Describe()}");
        });
        IsBusyServices = false;
    }

    /// <summary>Rebuilds what the clone has checked out now.</summary>
    [RelayCommand]
    private Task RedeployAsync(ServiceInfo s) =>
        RedeployWithAsync(s, "Redeployed", (force, onLine) => _services.RedeployAsync(s.AppName, false, null, force, onLine));

    /// <summary>Moves the clone to the newest version of what it follows, then rebuilds.</summary>
    [RelayCommand]
    private Task UpdateRedeployAsync()
    {
        if (SelectedService is not { } s) return Task.CompletedTask;
        return RedeployWithAsync(s, "Updated and redeployed",
            (force, onLine) => _services.RedeployAsync(s.AppName, true, null, force, onLine));
    }

    /// <summary>Checks out the chosen branch, tag, release or commit, then rebuilds - also how a service is rolled back.</summary>
    [RelayCommand]
    private Task SwitchVersionAsync()
    {
        if (SelectedService is not { } s) return Task.CompletedTask;

        GitSource source;
        try
        {
            source = VersionPicker.Build()!;
        }
        catch (OperationException ex)
        {
            SetStatus(ex.Message, isError: true);
            return Task.CompletedTask;
        }

        return RedeployWithAsync(s, $"Switched to {source.Describe()} and redeployed",
            (force, onLine) => _services.RedeployAsync(s.AppName, false, source, force, onLine));
    }

    private async Task RedeployWithAsync(ServiceInfo s, string done, Func<bool, Action<string>, Task<DeploymentInfo>> redeploy)
    {
        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var deployment = await ThenReloadAsync(
                () => WithForceRetryAsync(force => redeploy(force, Append), $"the clone of {s.AppName}"),
                LoadServicesAsync);

            Append($"\n✓ {done}");
            SetStatus($"{s.AppName}: {deployment.Describe()}");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task LoadVersionRefsAsync()
    {
        if (SelectedService is not { } s) return;
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            var repo = s.Deployment?.Repository
                       ?? (await _services.GetSourceProjectAsync(s.AppName) is { } csproj ? AppServiceManager.RepoNameOf(csproj) : null)
                       ?? throw OperationException.NotFound(
                           $"{s.AppName} was not built from a clone under {DeployPaths.Repos} - it has no versions to pick from.");
            VersionPicker.Load(await _repos.ListRefsAsync(repo));
            SetStatus(VersionPicker.RefsSummary);
        }, "Reading branches and tags...");
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
        VersionPicker.Reset();
        VersionPicker.Preset(GitSource.Parse(s.Deployment?.Source));
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
