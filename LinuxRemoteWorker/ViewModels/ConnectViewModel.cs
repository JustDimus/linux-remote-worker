using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using Microsoft.Win32;

namespace LinuxRemoteWorker.ViewModels;

public partial class ConnectViewModel : BaseViewModel
{
    private readonly SshService _ssh;
    private readonly ProfileService _profileService;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _username = "root";
    [ObservableProperty] private string _privateKeyPath = string.Empty;
    [ObservableProperty] private string _passphrase = string.Empty;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    /// <summary>The last connection failure, explained. Null when there is nothing to report.</summary>
    [ObservableProperty] private ConnectionProblem? _problem;

    public string LogFilePath => AppLog.CurrentFile;

    /// <summary>Shown in the saved-servers panel so profiles are findable on disk.</summary>
    public string ProfilesPath => ProfileService.ProfilesPath;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public event Action? ConnectedSuccessfully;

    public ConnectViewModel(SshService ssh)
    {
        _ssh = ssh;
        _profileService = new ProfileService();
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profileService.Load())
            Profiles.Add(p);
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value == null) return;
        Host = value.Host;
        Username = value.Username;
        PrivateKeyPath = value.PrivateKeyPath;
        ProfileName = value.Name;
        Problem = null;
    }

    [RelayCommand]
    private void BrowseKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "All files (*.*)|*.*|PEM files (*.pem)|*.pem"
        };
        if (dialog.ShowDialog() == true)
            PrivateKeyPath = dialog.FileName;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;

        var name = string.IsNullOrWhiteSpace(ProfileName) ? Host : ProfileName;

        var profiles = _profileService.Load();

        // Update existing or add new
        var existing = SelectedProfile != null
            ? profiles.FirstOrDefault(p => p.Id == SelectedProfile.Id)
            : null;

        if (existing != null)
        {
            existing.Name = name;
            existing.Host = Host;
            existing.Username = Username;
            existing.PrivateKeyPath = PrivateKeyPath;
        }
        else
        {
            profiles.Add(new ConnectionProfile
            {
                Name = name,
                Host = Host,
                Username = Username,
                PrivateKeyPath = PrivateKeyPath
            });
        }

        _profileService.Save(profiles);
        LoadProfiles();
        AppLog.Info($"Profile saved: {name} ({Username}@{Host})");
        SetStatus("Profile saved");
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;

        var profiles = _profileService.Load();
        profiles.RemoveAll(p => p.Id == SelectedProfile.Id);
        _profileService.Save(profiles);

        AppLog.Info($"Profile deleted: {SelectedProfile.Name}");
        SelectedProfile = null;
        LoadProfiles();
        SetStatus("Profile deleted");
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Problem = null;
        AppLog.Info($"---- Connect requested: {Username}@{Host} ----");

        var preflight = ConnectionDiagnostics.PreflightCheck(Host, Username, PrivateKeyPath);
        if (preflight != null)
        {
            ReportProblem(preflight);
            return;
        }

        IsBusy = true;
        HasError = false;
        SetStatus("Connecting...");
        try
        {
            await Task.Run(() => _ssh.Connect(Host, Username, PrivateKeyPath,
                string.IsNullOrEmpty(Passphrase) ? null : Passphrase));

            IsConnected = true;
            SetStatus($"Connected to {Host}");
            ConnectedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ReportProblem(ConnectionDiagnostics.Explain(ex, Host, Username, PrivateKeyPath));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportProblem(ConnectionProblem problem)
    {
        Problem = problem;
        AppLog.Warn($"Connection problem: {problem.Summary} | {problem.Detail}");
        SetStatus(problem.Summary, isError: true);
    }

    [RelayCommand]
    private void CopyProblem()
    {
        if (Problem == null) return;
        try
        {
            System.Windows.Clipboard.SetText(Problem.ToClipboardText(Host, Username, PrivateKeyPath));
            SetStatus("Error details copied to clipboard");
        }
        catch (Exception ex)
        {
            AppLog.Error("Clipboard copy failed", ex);
        }
    }

    [RelayCommand]
    private void OpenLog() => AppLog.OpenFile();

    [RelayCommand]
    private void OpenProfilesFolder() => ProfileService.RevealInExplorer();

    [RelayCommand]
    private void DismissProblem() => Problem = null;

    [RelayCommand]
    private void Disconnect()
    {
        _ssh.Disconnect();
        IsConnected = false;
        AppLog.Info("Disconnected by user");
        SetStatus("Disconnected");
    }
}
