using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Services;

public partial class ServicesViewModel : BaseViewModel, IModule
{
    public string Title => "Services";
    public string Icon => "⚙";

    private readonly SshService _ssh;
    private readonly BootstrapService _bootstrap;
    private CancellationTokenSource? _logCts;

    [ObservableProperty] private bool _isBusyServices;
    [ObservableProperty] private string _outputLog = string.Empty;

    // .NET SDK tooling
    [ObservableProperty] private bool _dotnetInstalled;
    [ObservableProperty] private string _dotnetVersion = string.Empty;
    [ObservableProperty] private string _installedSdks = string.Empty;
    [ObservableProperty] private string _installedRuntimes = string.Empty;
    [ObservableProperty] private string _selectedSdkVersion = "10.0";
    public List<string> SdkVersionOptions { get; } = ["10.0", "9.0", "8.0"];

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
        _ssh = ssh;
        _bootstrap = new BootstrapService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await _bootstrap.EnsureAsync();

            var dotnet = await _ssh.RunCommandAsync("dotnet --version 2>/dev/null || echo ''");
            DotnetInstalled = !string.IsNullOrWhiteSpace(dotnet);
            DotnetVersion = DotnetInstalled ? $"default SDK {dotnet.Trim()}" : string.Empty;

            if (DotnetInstalled)
            {
                InstalledSdks = await _ssh.RunCommandAsync("dotnet --list-sdks 2>/dev/null");
                InstalledRuntimes = await _ssh.RunCommandAsync("dotnet --list-runtimes 2>/dev/null");
            }
            else
            {
                InstalledSdks = string.Empty;
                InstalledRuntimes = string.Empty;
            }

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

            // All versions install into ONE DOTNET_ROOT so they coexist and are all
            // listed by `dotnet --list-sdks` (multilevel lookup was removed in .NET 7+).
            // Use Microsoft's install script (Azure CDN has IPv6, works without IPv4).
            const string root = "/usr/lib/dotnet";
            var script =
                $"curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh 2>&1 && " +
                $"bash /tmp/dotnet-install.sh --channel {version} --install-dir {root} 2>&1 && " +
                $"ln -sf {root}/dotnet /usr/bin/dotnet && " +
                $"echo \"[ok] .NET {version} installed into {root}\"";

            await _ssh.RunCommandStreamAsync(script, Append);
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
            const string root = "/usr/lib/dotnet";
            Append($"→ Removing .NET {version} (sdk + runtimes) from {root}...");
            await _ssh.RunCommandStreamAsync(
                $"rm -rf {root}/sdk/{version}.* " +
                $"{root}/shared/Microsoft.AspNetCore.App/{version}.* " +
                $"{root}/shared/Microsoft.NETCore.App/{version}.* 2>&1; " +
                $"echo '[ok] removed {version}'", Append);
            Append("\n✓ Done");
            await RefreshAsync();
        });
        IsBusyServices = false;
    }

    private async Task LoadReposAsync()
    {
        AvailableRepos.Clear();
        var names = await _ssh.RunCommandAsync($"ls -1 {DeployPaths.Repos} 2>/dev/null");
        foreach (var n in names.Split('\n').Where(n => !string.IsNullOrWhiteSpace(n)))
            AvailableRepos.Add(n.Trim());
    }

    private async Task LoadServicesAsync()
    {
        Services.Clear();
        var units = await _ssh.RunCommandAsync(
            $"systemctl list-unit-files '{DeployPaths.ServicePrefix}*.service' --no-legend 2>/dev/null | awk '{{print $1}}'");

        foreach (var unit in units.Split('\n').Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            var unitName = unit.Trim();
            var app = unitName.Replace(DeployPaths.ServicePrefix, "").Replace(".service", "");
            var active = await _ssh.RunCommandAsync($"systemctl is-active {unitName} 2>/dev/null || echo inactive");
            var enabled = await _ssh.RunCommandAsync($"systemctl is-enabled {unitName} 2>/dev/null || echo disabled");
            Services.Add(new ServiceInfo(
                app, unitName,
                active.Trim() == "active",
                enabled.Trim() == "enabled"));
        }
    }

    partial void OnSelectedRepoChanged(string? value)
    {
        _ = LoadCsprojAsync(value);
    }

    private async Task LoadCsprojAsync(string? repo)
    {
        CsprojFiles.Clear();
        if (string.IsNullOrWhiteSpace(repo)) return;

        var found = await _ssh.RunCommandAsync(
            $"find {DeployPaths.RepoDir(repo)} -name '*.csproj' 2>/dev/null");
        foreach (var f in found.Split('\n').Where(f => !string.IsNullOrWhiteSpace(f)))
            CsprojFiles.Add(f.Trim());
    }

    partial void OnSelectedCsprojChanged(string? value)
    {
        // Suggest app name from csproj file name
        if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(AppName))
        {
            var file = value.Split('/').Last();
            AppName = file.Replace(".csproj", "").ToLower().Replace(".", "-");
        }
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedCsproj) || string.IsNullOrWhiteSpace(AppName))
        {
            SetStatus("Select a .csproj and enter an app name", isError: true);
            return;
        }

        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var app = AppName.Trim();
            var appDir = DeployPaths.AppDir(app);
            var dotnetPath = (await _ssh.RunCommandAsync("which dotnet")).Trim();

            if (string.IsNullOrWhiteSpace(dotnetPath))
                throw new Exception(".NET SDK not found. Install it in the Repositories module first.");

            Append($"→ Publishing {SelectedCsproj}");
            Append($"  → {appDir}");
            await _ssh.RunCommandStreamAsync(
                $"{dotnetPath} publish '{SelectedCsproj}' -c Release -o {appDir} 2>&1", Append);

            // Resolve entry dll from runtimeconfig
            var rc = (await _ssh.RunCommandAsync(
                $"ls {appDir}/*.runtimeconfig.json 2>/dev/null | head -1")).Trim();
            if (string.IsNullOrWhiteSpace(rc))
                throw new Exception("Publish produced no runtimeconfig.json — is this an executable project?");
            var dll = rc.Split('/').Last().Replace(".runtimeconfig.json", ".dll");

            Append($"\n→ Entry point: {dll}");
            Append("→ Writing systemd unit...");

            var unit = BuildUnit(app, appDir, dotnetPath, dll, AspNetUrls.Trim());
            await WriteUnitAsync(app, unit);

            // Remember the source .csproj so Redeploy can rebuild without re-selecting
            await _ssh.RunCommandAsync($"echo '{SelectedCsproj}' > {appDir}/.lrw-source");

            await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {appDir}");
            await _ssh.RunCommandAsync("systemctl daemon-reload");
            await _ssh.RunCommandStreamAsync($"systemctl enable --now {DeployPaths.UnitName(app)} 2>&1", Append);

            Append("\n✓ Deployed and started");
            await LoadServicesAsync();
            SetStatus($"Service {app} deployed");
        });
        IsBusyServices = false;
    }

    private static string BuildUnit(string app, string appDir, string dotnetPath, string dll, string urls) =>
        $"""
        [Unit]
        Description={app} (managed by LinuxRemoteWorker)
        After=network.target

        [Service]
        WorkingDirectory={appDir}
        ExecStart={dotnetPath} {appDir}/{dll}
        Restart=always
        RestartSec=5
        User={DeployPaths.ServiceUser}
        Environment=ASPNETCORE_ENVIRONMENT=Production
        Environment=ASPNETCORE_URLS={urls}

        [Install]
        WantedBy=multi-user.target
        """;

    private async Task WriteUnitAsync(string app, string content)
    {
        // Write via heredoc; quoted delimiter prevents variable expansion
        var cmd = $"cat > {DeployPaths.UnitPath(app)} << 'LRWUNITEOF'\n{content}\nLRWUNITEOF";
        await _ssh.RunCommandAsync(cmd);
    }

    [RelayCommand]
    private async Task RedeployAsync(ServiceInfo s)
    {
        IsBusyServices = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var appDir = DeployPaths.AppDir(s.AppName);
            var csproj = (await _ssh.RunCommandAsync($"cat {appDir}/.lrw-source 2>/dev/null")).Trim();
            if (string.IsNullOrWhiteSpace(csproj))
                throw new Exception("No recorded source for this service. Use Build & Deploy once to set it up.");

            var dotnetPath = (await _ssh.RunCommandAsync("which dotnet")).Trim();

            Append($"→ Publishing {csproj}");
            await _ssh.RunCommandStreamAsync(
                $"{dotnetPath} publish '{csproj}' -c Release -o {appDir} 2>&1", Append);

            await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {appDir}");

            Append($"\n→ Restarting {s.UnitName} (unit untouched)");
            await _ssh.RunCommandAsync($"systemctl restart {s.UnitName}");

            Append("\n✓ Redeployed");
            await LoadServicesAsync();
            SetStatus($"{s.AppName} redeployed from latest");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task StartAsync(ServiceInfo s) => await ServiceActionAsync(s, "start");

    [RelayCommand]
    private async Task StopAsync(ServiceInfo s) => await ServiceActionAsync(s, "stop");

    [RelayCommand]
    private async Task RestartAsync(ServiceInfo s) => await ServiceActionAsync(s, "restart");

    private async Task ServiceActionAsync(ServiceInfo s, string action)
    {
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await _ssh.RunCommandAsync($"systemctl {action} {s.UnitName}");
            await LoadServicesAsync();
            SetStatus($"{s.AppName}: {action} done");
        });
        IsBusyServices = false;
    }

    [RelayCommand]
    private async Task DeleteServiceAsync(ServiceInfo s)
    {
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await _ssh.RunCommandAsync($"systemctl disable --now {s.UnitName} 2>/dev/null; rm -f {DeployPaths.UnitPath(s.AppName)}; systemctl daemon-reload");
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
        await RunSafeAsync(async () =>
        {
            UnitContent = await _ssh.RunCommandAsync($"cat {DeployPaths.UnitPath(s.AppName)} 2>/dev/null");
        });
    }

    [RelayCommand]
    private async Task SaveUnitAsync()
    {
        if (SelectedService == null) return;
        IsBusyServices = true;
        await RunSafeAsync(async () =>
        {
            await WriteUnitAsync(SelectedService.AppName, UnitContent.TrimEnd());
            await _ssh.RunCommandAsync("systemctl daemon-reload");
            await _ssh.RunCommandAsync($"systemctl restart {SelectedService.UnitName}");
            await LoadServicesAsync();
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
            LogOutput = await _ssh.RunCommandAsync(
                $"journalctl -u {SelectedService.UnitName} --since '{since}' --no-pager 2>&1 | tail -500");
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
            await _ssh.RunCommandStreamAsync(
                $"journalctl -u {SelectedService.UnitName} -f -n 50",
                line => LogOutput += line + "\n",
                _logCts.Token);
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
