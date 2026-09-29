using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;
using Microsoft.Win32;

namespace LinuxRemoteWorker.Modules.Logs;

public partial class LogsViewModel : BaseViewModel, IModule
{
    public string Title => "Logs";
    public string Icon => "📋";

    private readonly SshService _ssh;
    private readonly BootstrapService _bootstrap;
    private CancellationTokenSource? _logCts;

    [ObservableProperty] private bool _isBusyLogs;

    public ObservableCollection<string> Services { get; } = [];
    [ObservableProperty] private string? _selectedService;

    // Log dir + suggested file path for the app config
    [ObservableProperty] private string _logDir = string.Empty;
    [ObservableProperty] private string _suggestedLogFile = string.Empty;

    // File logs
    public ObservableCollection<string> LogFiles { get; } = [];
    [ObservableProperty] private string? _selectedLogFile;

    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private bool _isStreaming;

    public LogsViewModel(SshService ssh)
    {
        _ssh = ssh;
        _bootstrap = new BootstrapService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyLogs = true;
        await RunSafeAsync(async () =>
        {
            await _bootstrap.EnsureAsync();
            Services.Clear();
            var units = await _ssh.RunCommandAsync(
                $"systemctl list-unit-files '{DeployPaths.ServicePrefix}*.service' --no-legend 2>/dev/null | awk '{{print $1}}'");
            foreach (var u in units.Split('\n').Where(u => !string.IsNullOrWhiteSpace(u)))
                Services.Add(u.Trim().Replace(DeployPaths.ServicePrefix, "").Replace(".service", ""));
        });
        IsBusyLogs = false;
    }

    partial void OnSelectedServiceChanged(string? value)
    {
        _ = OnServiceSelectedAsync(value);
    }

    private async Task OnServiceSelectedAsync(string? app)
    {
        Output = string.Empty;
        LogFiles.Clear();
        if (string.IsNullOrWhiteSpace(app))
        {
            LogDir = SuggestedLogFile = string.Empty;
            return;
        }

        LogDir = DeployPaths.LogDir(app);
        SuggestedLogFile = $"{LogDir}/{app}.log";

        await RunSafeAsync(async () =>
        {
            // Ensure the per-app log dir exists and is writable by the service user
            await _ssh.RunCommandAsync(
                $"mkdir -p {LogDir} && chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {LogDir}");
            await LoadLogFilesAsync(app);
        });
    }

    private async Task LoadLogFilesAsync(string app)
    {
        LogFiles.Clear();
        var files = await _ssh.RunCommandAsync(
            $"ls -1t {DeployPaths.LogDir(app)} 2>/dev/null");
        foreach (var f in files.Split('\n').Where(f => !string.IsNullOrWhiteSpace(f)))
            LogFiles.Add(f.Trim());
    }

    private string UnitName => $"{DeployPaths.ServicePrefix}{SelectedService}.service";

    // ---- journalctl (systemd captured stdout/stderr) ----

    [RelayCommand]
    private async Task JournalHourAsync() => await JournalAsync("1 hour ago");

    [RelayCommand]
    private async Task JournalDayAsync() => await JournalAsync("1 day ago");

    private async Task JournalAsync(string since)
    {
        if (string.IsNullOrWhiteSpace(SelectedService)) return;
        await RunSafeAsync(async () =>
        {
            Output = await _ssh.RunCommandAsync(
                $"journalctl -u {UnitName} --since '{since}' --no-pager 2>&1 | tail -1000");
            if (string.IsNullOrWhiteSpace(Output)) Output = "(no journal entries)";
        });
    }

    [RelayCommand]
    private async Task JournalLiveAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedService) || IsStreaming) return;
        _logCts = new CancellationTokenSource();
        IsStreaming = true;
        Output = string.Empty;
        try
        {
            await _ssh.RunCommandStreamAsync(
                $"journalctl -u {UnitName} -f -n 50",
                line => Output += line + "\n",
                _logCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message, isError: true); }
        finally { IsStreaming = false; }
    }

    [RelayCommand]
    private void StopLive()
    {
        _logCts?.Cancel();
        IsStreaming = false;
    }

    // ---- File logs ----

    [RelayCommand]
    private async Task ViewFileAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedService) || string.IsNullOrWhiteSpace(SelectedLogFile)) return;
        await RunSafeAsync(async () =>
        {
            var path = $"{DeployPaths.LogDir(SelectedService)}/{SelectedLogFile}";
            Output = await _ssh.RunCommandAsync($"tail -n 1000 '{path}' 2>&1");
            if (string.IsNullOrWhiteSpace(Output)) Output = "(file is empty)";
        });
    }

    [RelayCommand]
    private async Task DownloadFileAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedService) || string.IsNullOrWhiteSpace(SelectedLogFile)) return;

        var dialog = new SaveFileDialog { FileName = SelectedLogFile, Title = "Save log file" };
        if (dialog.ShowDialog() != true) return;

        await RunSafeAsync(async () =>
        {
            var remote = $"{DeployPaths.LogDir(SelectedService)}/{SelectedLogFile}";
            await _ssh.DownloadFileAsync(remote, dialog.FileName);
            SetStatus($"Downloaded to {dialog.FileName}");
        });
    }

    [RelayCommand]
    private async Task RefreshFilesAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedService)) return;
        await RunSafeAsync(async () => await LoadLogFilesAsync(SelectedService));
    }

    [RelayCommand]
    private void CopyLogDir()
    {
        if (!string.IsNullOrEmpty(LogDir))
        {
            System.Windows.Clipboard.SetText(LogDir);
            SetStatus("Log directory path copied!");
        }
    }

    [RelayCommand]
    private void CopySuggestedFile()
    {
        if (!string.IsNullOrEmpty(SuggestedLogFile))
        {
            System.Windows.Clipboard.SetText(SuggestedLogFile);
            SetStatus("Log file path copied!");
        }
    }
}
