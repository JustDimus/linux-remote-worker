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
        SetStatus("Profile saved");
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;

        var profiles = _profileService.Load();
        profiles.RemoveAll(p => p.Id == SelectedProfile.Id);
        _profileService.Save(profiles);

        SelectedProfile = null;
        LoadProfiles();
        SetStatus("Profile deleted");
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            SetStatus("Fill in all fields", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            SetStatus("Connecting...");
            await Task.Run(() => _ssh.Connect(Host, Username, PrivateKeyPath, string.IsNullOrEmpty(Passphrase) ? null : Passphrase));
            IsConnected = true;
            SetStatus($"Connected to {Host}");
            ConnectedSuccessfully?.Invoke();
        });
    }

    [RelayCommand]
    private void Disconnect()
    {
        _ssh.Disconnect();
        IsConnected = false;
        SetStatus("Disconnected");
    }
}
