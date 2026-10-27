using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;
using Microsoft.Win32;

namespace LinuxRemoteWorker.Modules.Security;

public partial class SecurityViewModel : BaseViewModel, IModule
{
    public string Title => "Security";
    public string Icon => "🔐";

    private readonly SshService _ssh;
    private readonly SecurityService _security;
    private readonly Func<string, string?, string?, Task<bool>> _switchToKey;
    private string? _createdKeyPassphrase;
    private string? _overwriteApprovedFor;

    [ObservableProperty] private bool _isBusySecurity;
    [ObservableProperty] private string _outputLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText), nameof(HostKeyText), nameof(PasswordLoginText), nameof(PasswordLoginAllowed),
        nameof(PasswordAuthText), nameof(InteractiveText), nameof(PubkeyText), nameof(RootLoginText), nameof(PolicyInstalled),
        nameof(CanDisablePasswordLogin), nameof(DisableBlockedReason), nameof(CanUseCreatedKey), nameof(PrivilegesText),
        nameof(NoPrivileges), nameof(IsRoot), nameof(RootLoginStateText), nameof(RootLoginAllowed), nameof(RootPolicyInstalled),
        nameof(CanDisableRootLogin), nameof(DisableRootBlockedReason), nameof(CanSwitchToAdmin))]
    private SecurityStatus? _status;

    // Admin user
    [ObservableProperty] private string _adminUser = string.Empty;
    [ObservableProperty] private bool _copyKeys = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitchToAdmin))]
    private CreatedAdmin? _createdAdmin;

    [ObservableProperty] private string _keyPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseCreatedKey))]
    private CreatedKey? _createdKey;

    public ObservableCollection<AuthorizedKey> AuthorizedKeys { get; } = [];

    // This computer's existing key
    [ObservableProperty] private string _myKeyPath = DefaultMyKey();

    /// <summary>Set by the view's password box.</summary>
    public string MyKeyPassphrase { get; set; } = string.Empty;

    // Another device's public key
    [ObservableProperty] private string _publicKeyText = string.Empty;
    [ObservableProperty] private string _publicKeyUser = string.Empty;
    [ObservableProperty] private string _publicKeyPreview = string.Empty;
    [ObservableProperty] private bool _publicKeyInvalid;

    /// <param name="switchToKey">
    /// Moves the login (profile and live session) to a key file - and to another user, with a passphrase, when given;
    /// true when a profile was updated.
    /// </param>
    public SecurityViewModel(SshService ssh, Func<string, string?, string?, Task<bool>> switchToKey)
    {
        _ssh = ssh;
        _security = new SecurityService(ssh);
        _switchToKey = switchToKey;
    }

    private static string DefaultMyKey()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? string.Empty;
    }

    // Shows the fingerprint while typing, so it can be compared with the other device before adding
    partial void OnPublicKeyTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            PublicKeyPreview = string.Empty;
            PublicKeyInvalid = false;
            return;
        }
        try
        {
            var key = SshPublicKey.Parse(value);
            PublicKeyPreview = $"{key.Type} {key.Fingerprint} {key.Comment}";
            PublicKeyInvalid = false;
        }
        catch (OperationException ex)
        {
            PublicKeyPreview = ex.Message;
            PublicKeyInvalid = true;
        }
    }

    public string SessionText => Status == null ? string.Empty
        : Status.SessionAuth == SshAuthMethod.Key
            ? $"Logged in as {Status.User} with the SSH key {Status.SessionKeyPath}"
            : $"Logged in as {Status.User} with a password";

    public string HostKeyText => Status?.HostKey is { } key ? $"Server key: {key}" : string.Empty;

    public bool PasswordLoginAllowed => Status?.PasswordLoginAllowed ?? false;

    public string PasswordLoginText => Status == null ? string.Empty
        : Status.PasswordLoginAllowed ? "Password logins are allowed" : "Password logins are off - only SSH keys get in";

    public string PasswordAuthText => YesNo(Status?.Sshd.PasswordAuthentication);
    public string InteractiveText => YesNo(Status?.Sshd.KbdInteractiveAuthentication);
    public string PubkeyText => YesNo(Status?.Sshd.PubkeyAuthentication);
    public string RootLoginText => Status?.Sshd.PermitRootLogin ?? "-";

    public bool PolicyInstalled => Status?.PolicyInstalled ?? false;

    public bool CanDisablePasswordLogin => Status is { CannotDisablePasswordLogin: null };

    /// <summary>Shown under the disabled button while passwords are still allowed.</summary>
    public string? DisableBlockedReason => Status is { PasswordLoginAllowed: true } ? Status.CannotDisablePasswordLogin : null;

    public bool CanUseCreatedKey => CreatedKey != null && Status?.SessionKeyPath != CreatedKey.LocalPath;

    public string PrivilegesText => Status?.Privileges switch
    {
        null => string.Empty,
        "root" => "Commands run as root.",
        "sudo" => $"Commands run as root through {Status.User}'s passwordless sudo.",
        _ => $"⚠ {Status.User} is not root and has no passwordless sudo - changes to the server will fail."
    };

    public bool NoPrivileges => Status?.Privileges == "none";

    public bool IsRoot => Status?.IsRoot ?? false;

    public string RootLoginStateText => Status == null ? string.Empty
        : Status.RootLoginAllowed
            ? $"Root can log in over SSH (PermitRootLogin {Status.RootLogin ?? "default"})"
            : "Root login is off - administer the server as a user with sudo";

    public bool RootLoginAllowed => Status?.RootLoginAllowed ?? false;
    public bool RootPolicyInstalled => Status?.RootPolicyInstalled ?? false;
    public bool CanDisableRootLogin => Status is { CannotDisableRootLogin: null };
    public string? DisableRootBlockedReason => Status is { RootLoginAllowed: true } ? Status.CannotDisableRootLogin : null;
    public bool CanSwitchToAdmin => CreatedAdmin != null && Status?.User != CreatedAdmin.User;

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", _ => "-" };

    public async Task LoadAsync(SshService ssh)
    {
        // Another server: suggest a key file named after it, unless the user picked a path themselves
        if (ssh.Host != null && ssh.Host != _keyPathHost)
        {
            if (string.IsNullOrWhiteSpace(KeyPath) || KeyPath == _suggestedKeyPath)
                KeyPath = _suggestedKeyPath = SecurityService.DefaultKeyPath(ssh.Host);
            _keyPathHost = ssh.Host;
            CreatedKey = null;
            CreatedAdmin = null;
        }
        await RefreshAsync();
    }

    private string? _keyPathHost;
    private string? _suggestedKeyPath;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusySecurity = true;
        await RunSafeAsync(LoadStatusAsync, "Reading the SSH login settings...");
        IsBusySecurity = false;
    }

    private async Task LoadStatusAsync() => ShowStatus(await _security.GetStatusAsync());

    private void ShowStatus(SecurityStatus status)
    {
        Status = status;
        AuthorizedKeys.Clear();
        foreach (var key in status.AuthorizedKeys) AuthorizedKeys.Add(key);
    }

    [RelayCommand]
    private void BrowseKeyPath()
    {
        var current = string.IsNullOrWhiteSpace(KeyPath) ? SecurityService.DefaultKeyPath(_ssh.Host ?? "server") : KeyPath;
        var dialog = new SaveFileDialog
        {
            Title = "Save the new private key as",
            FileName = Path.GetFileName(current),
            InitialDirectory = Path.GetDirectoryName(current),
            Filter = "All files (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        KeyPath = dialog.FileName;
        // The dialog already asked whether to replace an existing file
        _overwriteApprovedFor = File.Exists(dialog.FileName) ? dialog.FileName : null;
    }

    [RelayCommand]
    private async Task CreateKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(KeyPath))
        {
            SetStatus("Choose where to save the private key", isError: true);
            return;
        }

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var path = KeyPath.Trim();
            CreatedKey = await ThenReloadAsync(
                () => _security.CreateKeyAsync(path, overwrite: _overwriteApprovedFor == path, Append),
                LoadStatusAsync);
            _createdKeyPassphrase = null;
            _overwriteApprovedFor = null;
            SetStatus($"Key created and tested: {CreatedKey.LocalPath}");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task UseCreatedKeyAsync()
    {
        if (CreatedKey is not { } key) return;

        IsBusySecurity = true;
        await RunSafeAsync(async () =>
        {
            var profileUpdated = await _switchToKey(key.LocalPath, null, _createdKeyPassphrase);
            await LoadStatusAsync();
            SetStatus(profileUpdated
                ? "Reconnected with the key and saved it in the profile"
                : "Reconnected with the key (this connection has no saved profile - save one on the Connection screen)");
        }, "Reconnecting with the key...");
        IsBusySecurity = false;
    }

    [RelayCommand]
    private void BrowseMyKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select your private key",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "All files (*.*)|*.*|PuTTY keys (*.ppk)|*.ppk"
        };
        if (dialog.ShowDialog() == true) MyKeyPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task AddMyKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(MyKeyPath))
        {
            SetStatus("Choose your private key file", isError: true);
            return;
        }

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var passphrase = MyKeyPassphrase.Length > 0 ? MyKeyPassphrase : null;
            CreatedKey = await ThenReloadAsync(() => _security.AddMyKeyAsync(MyKeyPath, passphrase, Append), LoadStatusAsync);
            _createdKeyPassphrase = passphrase;
            SetStatus($"Your key logs in: {CreatedKey.LocalPath}");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private void BrowsePublicKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the other device's public key (.pub)",
            Filter = "Public keys (*.pub)|*.pub|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            PublicKeyText = File.ReadAllText(dialog.FileName).Trim();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task AddPublicKeyAsync()
    {
        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var user = string.IsNullOrWhiteSpace(PublicKeyUser) ? null : PublicKeyUser.Trim();
            var added = await ThenReloadAsync(() => _security.AddPublicKeyAsync(PublicKeyText, user, Append), LoadStatusAsync);
            PublicKeyText = string.Empty;
            SetStatus(added.AlreadyThere
                ? $"{added.Key.Fingerprint} could already log in as {added.User}"
                : $"{added.Key.Fingerprint} can now log in as {added.User} - check that the other device shows the same fingerprint");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task RemoveKeyAsync(AuthorizedKey key)
    {
        if (!Confirm($"Stop this key from logging in as {Status?.User}?\n\n{key.Type} {key.Fingerprint}\n{key.Comment}\n\n" +
                     "Sessions already open with it stay connected until they end. The key you are logged in with cannot be removed.",
                "Remove key"))
            return;

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            ShowStatus(await ThenReloadAsync(() => _security.RemoveKeyAsync(key.Fingerprint, Append), LoadStatusAsync));
            SetStatus($"{key.Fingerprint} can no longer log in");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task CreateAdminAsync()
    {
        var user = AdminUser.Trim();
        if (SecurityService.ValidateNewUserName(user) is { } error)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var keyPath = SecurityService.DefaultKeyPath(_ssh.Host ?? "server", user);
            CreatedAdmin = await ThenReloadAsync(
                () => _security.CreateAdminUserAsync(user, keyPath, CopyKeys, overwrite: false, Append),
                LoadStatusAsync);
            SetStatus($"{CreatedAdmin.User} created with passwordless sudo - key saved to {CreatedAdmin.Key.LocalPath}");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task SwitchToAdminAsync()
    {
        if (CreatedAdmin is not { } admin) return;

        IsBusySecurity = true;
        await RunSafeAsync(async () =>
        {
            var profileUpdated = await _switchToKey(admin.Key.LocalPath, admin.User, null);
            await LoadStatusAsync();
            SetStatus(profileUpdated
                ? $"Reconnected as {admin.User} and saved it in the profile - root login can now be turned off"
                : $"Reconnected as {admin.User} (this connection has no saved profile - save one on the Connection screen)");
        }, $"Reconnecting as {admin.User}...");
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task DisableRootLoginAsync()
    {
        if (!Confirm($"Forbid SSH logins as root on {_ssh.Host}?\n\n" +
                     $"Administer the server as {Status?.User} with sudo from now on. Saved profiles that log in as root stop " +
                     "working. The app checks the configuration, reloads sshd, tests a fresh login with sudo and undoes " +
                     "everything if that fails.", "Disable root login"))
            return;

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            ShowStatus(await ThenReloadAsync(() => _security.DisableRootLoginAsync(Append), LoadStatusAsync));
            SetStatus("Root login is off");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task EnableRootLoginAsync()
    {
        if (!Confirm($"Allow SSH logins as root on {_ssh.Host} again?", "Allow root login"))
            return;

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            ShowStatus(await ThenReloadAsync(() => _security.EnableRootLoginAsync(Append), LoadStatusAsync));
            SetStatus(RootLoginAllowed
                ? "Root login is allowed again"
                : "The app's setting was removed, but the server's own sshd configuration still forbids root login");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task DisablePasswordLoginAsync()
    {
        if (!Confirm($"Turn password logins off on {_ssh.Host}?\n\n" +
                     "Only SSH keys will get in - for every user. The app checks the configuration, reloads sshd, tests a fresh " +
                     "login with your key and undoes everything if that fails. Open sessions stay connected.",
                "Disable password login"))
            return;

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            ShowStatus(await ThenReloadAsync(() => _security.DisablePasswordLoginAsync(Append), LoadStatusAsync));
            SetStatus("Password login is off - only SSH keys get in");
        });
        IsBusySecurity = false;
    }

    [RelayCommand]
    private async Task EnablePasswordLoginAsync()
    {
        if (!Confirm($"Allow password logins on {_ssh.Host} again?\n\nThis makes the server easier to break into by guessing passwords.",
                "Allow password login"))
            return;

        IsBusySecurity = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            ShowStatus(await ThenReloadAsync(() => _security.EnablePasswordLoginAsync(Append), LoadStatusAsync));
            SetStatus(PasswordLoginAllowed
                ? "Password login is allowed again"
                : "The app's setting was removed, but the server's own sshd configuration still turns password login off");
        });
        IsBusySecurity = false;
    }
}
