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

    // Password login
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseKey))]
    private bool _usePassword;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _rememberPassword;

    /// <summary>The selected profile has a saved password, used when the password field is left empty.</summary>
    [ObservableProperty] private bool _hasSavedPassword;

    /// <summary>The last connection failure, explained. Null when there is nothing to report.</summary>
    [ObservableProperty] private ConnectionProblem? _problem;

    /// <summary>The last failure was a changed server key, which the user may accept after checking it.</summary>
    [ObservableProperty] private bool _canTrustNewHostKey;

    private HostKeyChangedException? _changedHostKey;

    public bool UseKey
    {
        get => !UsePassword;
        set => UsePassword = !value;
    }

    public string LogFilePath => AppLog.CurrentFile;

    /// <summary>Shown in the saved-servers panel so profiles are findable on disk.</summary>
    public string ProfilesPath => ProfileService.ProfilesPath;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public event Action? ConnectedSuccessfully;

    /// <summary>Asks the view to empty its password boxes (they cannot be bound).</summary>
    public event Action? ClearSecrets;

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
        UsePassword = value.AuthMethod == SshAuthMethod.Password;
        HasSavedPassword = value.HasSavedPassword;
        RememberPassword = value.HasSavedPassword;
        Problem = null;
        CanTrustNewHostKey = false;
        // Another server's password or passphrase must not be sent to this one
        ClearSecrets?.Invoke();
    }

    // The saved password belongs to the profile's host and user; edited fields mean another login
    private string? SavedPassword() =>
        SelectedProfile is { HasSavedPassword: true } p && p.Host == Host.Trim() && p.Username == Username.Trim()
            ? PasswordProtector.Unprotect(p.ProtectedPassword)
            : null;

    private SshCredentials Credentials() => UsePassword
        ? SshCredentials.WithPassword(Host.Trim(), Username.Trim(), Password.Length > 0 ? Password : SavedPassword())
        : SshCredentials.WithKey(Host.Trim(), Username.Trim(), PrivateKeyPath.Trim(), Passphrase);

    [RelayCommand]
    private void BrowseKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "All files (*.*)|*.*|PEM files (*.pem)|*.pem|PuTTY keys (*.ppk)|*.ppk"
        };
        if (dialog.ShowDialog() == true)
            PrivateKeyPath = dialog.FileName;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;

        var name = string.IsNullOrWhiteSpace(ProfileName) ? Host : ProfileName;
        try
        {
            // Strict: saving after a failed read would replace every stored profile
            var profiles = _profileService.LoadStrict();

            // Update existing or add new
            var profile = SelectedProfile != null ? profiles.FirstOrDefault(p => p.Id == SelectedProfile.Id) : null;
            if (profile == null)
            {
                profile = new ConnectionProfile();
                profiles.Add(profile);
            }

            profile.Name = name;
            profile.Host = Host.Trim();
            profile.Username = Username.Trim();
            if (!UsePassword)
                profile.UseKey(PrivateKeyPath.Trim());
            else if (!RememberPassword)
                profile.UsePassword(null);
            else if (Password.Length > 0)
                profile.UsePassword(Password);
            else
                profile.AuthMethod = SshAuthMethod.Password; // keep the password saved earlier, if any

            _profileService.Save(profiles);
            AppLog.Info($"Profile saved: {name} ({Username}@{Host}, {(UsePassword ? "password" : "key")} login)");
            LoadProfiles();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
            SetStatus(UsePassword && RememberPassword && !profile.HasSavedPassword
                ? "Profile saved - type the password once with Remember password checked to save it"
                : "Profile saved");
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving the profile failed", ex);
            SetStatus(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        try
        {
            var profiles = _profileService.LoadStrict();
            profiles.RemoveAll(p => p.Id == SelectedProfile.Id);
            _profileService.Save(profiles);

            AppLog.Info($"Profile deleted: {SelectedProfile.Name}");
            SelectedProfile = null;
            LoadProfiles();
            SetStatus("Profile deleted");
        }
        catch (Exception ex)
        {
            AppLog.Error("Deleting the profile failed", ex);
            SetStatus(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Problem = null;
        CanTrustNewHostKey = false;
        var credentials = Credentials();
        AppLog.Info($"---- Connect requested: {credentials} ----");

        var preflight = ConnectionDiagnostics.PreflightCheck(credentials);
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
            await Task.Run(() => _ssh.Connect(credentials));

            IsConnected = true;
            SetStatus($"Connected to {Host}" + (_ssh.ServerKeyIsNew ? $" · server key remembered: {_ssh.ServerKey}" : "") + _ssh.Privileges switch
            {
                "sudo" => " · commands run through sudo",
                "none" => " · ⚠ not root and no passwordless sudo: changes to the server will fail",
                _ => ""
            });
            RememberTypedPassword();
            ConnectedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            _changedHostKey = ex as HostKeyChangedException;
            CanTrustNewHostKey = _changedHostKey != null;
            ReportProblem(ConnectionDiagnostics.Explain(ex, credentials));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // "Remember password" + a password that just worked: keep it with the selected profile
    private void RememberTypedPassword()
    {
        if (!UsePassword || !RememberPassword || Password.Length == 0 || SelectedProfile is not { } selected
            || selected.Host != Host.Trim() || selected.Username != Username.Trim())
            return;
        try
        {
            var profiles = _profileService.LoadStrict();
            if (profiles.FirstOrDefault(p => p.Id == selected.Id) is not { } profile) return;
            profile.UsePassword(Password);
            _profileService.Save(profiles);
            selected.AuthMethod = SshAuthMethod.Password;
            selected.ProtectedPassword = profile.ProtectedPassword;
            HasSavedPassword = true;
            AppLog.Info($"Password saved with profile {profile.Name}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving the password with the profile failed", ex);
        }
    }

    [RelayCommand]
    private async Task TrustNewHostKeyAsync()
    {
        if (_changedHostKey is not { } changed) return;
        if (!Confirm($"Trust the new SSH key of {changed.Host}?\n\n" +
                     $"Remembered since {changed.Known.FirstSeenUtc:yyyy-MM-dd}:\n{changed.Known.Key}\n\n" +
                     $"Presented now:\n{changed.Seen}\n\n" +
                     "Only continue if you know why it changed (server reinstalled, keys regenerated) - ideally after " +
                     "comparing the new fingerprint with the hosting console.", "Server key changed"))
            return;

        _ssh.HostKeys.Forget(changed.Host);
        _changedHostKey = null;
        await ConnectAsync();
    }

    /// <summary>
    /// Switches this login to a key made on the Security screen - and to another user, for a new admin user: the
    /// form, the selected profile when it is this server's, and the live session, which reconnects. Returns whether
    /// a profile was updated.
    /// </summary>
    public async Task<bool> SwitchToKeyAsync(string keyPath, string? username = null, string? passphrase = null)
    {
        var selected = SelectedProfile is { } p && p.Host == Host.Trim() && p.Username == Username.Trim() ? p : null;
        UsePassword = false;
        PrivateKeyPath = keyPath;
        if (username != null) Username = username;
        ClearSecrets?.Invoke();
        // After clearing the boxes, which reset the passphrase; it is kept in memory only, as when typed
        Passphrase = passphrase ?? string.Empty;

        var profileUpdated = false;
        if (selected != null)
        {
            var profiles = _profileService.LoadStrict();
            if (profiles.FirstOrDefault(p => p.Id == selected.Id) is { } profile)
            {
                profile.Username = Username.Trim();
                profile.UseKey(keyPath);
                _profileService.Save(profiles);
                AppLog.Info($"Profile {profile.Name} switched to key login as {profile.Username}: {keyPath}");
                LoadProfiles();
                SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
                profileUpdated = true;
            }
        }

        var credentials = Credentials();
        try
        {
            await Task.Run(() => _ssh.Connect(credentials));
        }
        catch
        {
            // The old session is gone too: lock the modules until the user connects again
            IsConnected = false;
            throw;
        }
        SetStatus($"Connected to {Host} as {Username} with the key {keyPath}");
        return profileUpdated;
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
            System.Windows.Clipboard.SetText(Problem.ToClipboardText(Credentials()));
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
