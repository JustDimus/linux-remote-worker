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

    private const int TailLines = 1000;

    private readonly ServerLogService _logs;
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
        _logs = new ServerLogService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyLogs = true;
        await RunSafeAsync(async () =>
        {
            var apps = await _logs.ListAppsAsync();
            Services.Clear();
            foreach (var app in apps) Services.Add(app);
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
            await _logs.EnsureLogDirAsync(app);
            await LoadLogFilesAsync(app);
        });
    }

    private async Task LoadLogFilesAsync(string app)
    {
        var files = await _logs.ListLogFilesAsync(app);
        LogFiles.Clear();
        foreach (var f in files) LogFiles.Add(f.Name);
    }

    private string UnitName => DeployPaths.UnitName(SelectedService!);

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
            Output = await _logs.JournalAsync(UnitName, since, TailLines);
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
            await _logs.FollowJournalAsync(UnitName, 50, line => Output += line + "\n", _logCts.Token);
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
        if (SelectedService is not { Length: > 0 } app || SelectedLogFile is not { Length: > 0 } file) return;
        Output = string.Empty; // on failure the status line explains; do not leave the previous file on screen
        await RunSafeAsync(async () =>
        {
            Output = await _logs.TailLogFileAsync(app, file, TailLines);
            if (string.IsNullOrWhiteSpace(Output)) Output = "(file is empty)";
        });
    }

    [RelayCommand]
    private async Task DownloadFileAsync()
    {
        if (SelectedService is not { Length: > 0 } app || SelectedLogFile is not { Length: > 0 } file) return;

        var dialog = new SaveFileDialog { FileName = file, Title = "Save log file" };
        if (dialog.ShowDialog() != true) return;

        await RunSafeAsync(async () =>
        {
            await _logs.DownloadLogFileAsync(app, file, dialog.FileName);
            SetStatus($"Downloaded to {dialog.FileName}");
        });
    }

    [RelayCommand]
    private async Task RefreshFilesAsync()
    {
        if (SelectedService is not { Length: > 0 } app) return;
        await RunSafeAsync(async () => await LoadLogFilesAsync(app));
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
