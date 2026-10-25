using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Postgres;

public partial class PostgresViewModel : BaseViewModel, IModule
{
    public string Title => "PostgreSQL";
    public string Icon => "🐘";

    private readonly PostgresService _pg;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _serviceStatus = string.Empty;
    [ObservableProperty] private string _installLog = string.Empty;
    [ObservableProperty] private bool _isInstalling;

    // True only when busy AND already installed (Refresh/Restart) — not during install
    [ObservableProperty] private bool _isBusyInstalled;

    // Config (read from the running server with SHOW, written with ALTER SYSTEM)
    [ObservableProperty] private string _listenAddresses = "*";
    [ObservableProperty] private string _port = "5432";

    // pg_hba entries
    [ObservableProperty] private string _newAllowIp = string.Empty;
    [ObservableProperty] private string _newAllowUser = "all";
    [ObservableProperty] private string _newAllowDb = "all";
    [ObservableProperty] private string _newAllowMethod = "scram-sha-256";
    public IReadOnlyList<string> AuthMethods => PgCommands.AuthMethods;
    public ObservableCollection<HbaRule> HbaRules { get; } = [];

    // Users
    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private bool _newUserSuperuser;
    public ObservableCollection<PgUser> Users { get; } = [];

    // Databases
    [ObservableProperty] private string _newDbName = string.Empty;
    [ObservableProperty] private string _newDbOwner = "postgres";
    public ObservableCollection<string> Databases { get; } = [];

    // Grant access
    [ObservableProperty] private string? _grantDb;
    [ObservableProperty] private string? _grantUser;
    [ObservableProperty] private string _grantLevel = "Read-write";
    public List<string> AccessLevels { get; } = ["Owner", "Read-write", "Read-only"];

    // Connection string
    [ObservableProperty] private string _connStringDb = "postgres";
    [ObservableProperty] private string _connStringUser = "postgres";
    [ObservableProperty] private string _connStringPassword = string.Empty;
    [ObservableProperty] private string _connectionString = string.Empty;
    [ObservableProperty] private string _connHostMode = "localhost";
    public List<string> ConnHostModes { get; } = ["localhost", "Server IPv4", "Server IPv6"];

    public PostgresViewModel(SshService ssh)
    {
        _pg = new PostgresService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyInstalled = IsInstalled;
        await RunSafeAsync(LoadAllAsync);
        IsBusyInstalled = false;
    }

    private async Task LoadAllAsync()
    {
        var status = await _pg.GetStatusAsync();
        IsInstalled = status.Installed;
        if (!IsInstalled) return;

        Version = status.Version ?? string.Empty;
        ServiceStatus = status.Online ? "active" : "down";
        if (!status.Online)
        {
            SetStatus("PostgreSQL is installed but not running - press Restart.", isError: true);
            return;
        }
        if (status.Settings is not { } settings)
        {
            SetStatus(status.Problem ?? "PostgreSQL settings could not be read.", isError: true);
            return;
        }

        ListenAddresses = settings.ListenAddresses;
        Port = settings.Port.ToString();
        await LoadHbaRulesAsync();
        await LoadUsersAsync();
        await LoadDatabasesAsync();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        InstallLog = string.Empty;

        await RunSafeAsync(async () =>
        {
            void Append(string line) => InstallLog += line + "\n";

            Append("→ Installing postgresql...");
            await _pg.InstallAsync(Append);

            Append("\n✓ Done!");
            await LoadAllAsync();
        });

        IsInstalling = false;
    }

    private async Task LoadHbaRulesAsync()
    {
        var rules = await _pg.ListHbaRulesAsync();
        HbaRules.Clear();
        foreach (var rule in rules) HbaRules.Add(rule);
    }

    [RelayCommand]
    private async Task SaveListenAddressAsync()
    {
        // Rejected input never reaches the server, and the form keeps what was typed
        if (PostgresService.ValidateNetwork(ListenAddresses, Port) is { } error)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyInstalled = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Applying and restarting PostgreSQL...");
            var listening = await ThenReloadAsync(() => _pg.SetNetworkAsync(ListenAddresses, Port), LoadAllAsync);
            SetStatus($"Saved & restarted. Listening on: {string.Join(", ", listening)}");
        }, "Applying and restarting PostgreSQL...");
        IsBusyInstalled = false;
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        IsBusyInstalled = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Restarting PostgreSQL...");
            await ThenReloadAsync(_pg.RestartAsync, LoadAllAsync);
            if (ServiceStatus == "active") SetStatus("PostgreSQL restarted");
        });
        IsBusyInstalled = false;
    }

    [RelayCommand]
    private async Task AddHbaRuleAsync()
    {
        if (PostgresService.ValidateHbaRule(NewAllowIp, NewAllowUser, NewAllowDb, NewAllowMethod).Error is { } error)
        {
            SetStatus(error, isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            var address = await ThenReloadAsync(
                () => _pg.AddHbaRuleAsync(NewAllowIp, NewAllowUser, NewAllowDb, NewAllowMethod), LoadHbaRulesAsync);
            NewAllowIp = string.Empty;
            SetStatus($"Rule added for {address} and PostgreSQL reloaded");
        });
    }

    [RelayCommand]
    private async Task RemoveHbaRuleAsync(HbaRule rule)
    {
        await RunSafeAsync(async () =>
        {
            await ThenReloadAsync(() => _pg.RemoveHbaRuleAsync(rule), LoadHbaRulesAsync);
            SetStatus("Rule removed");
        });
    }

    private async Task LoadUsersAsync()
    {
        var users = await _pg.ListUsersAsync();
        Users.Clear();
        foreach (var user in users) Users.Add(user);
    }

    private async Task LoadDatabasesAsync()
    {
        var databases = await _pg.ListDatabasesAsync();
        Databases.Clear();
        foreach (var db in databases) Databases.Add(db);
    }

    [RelayCommand]
    private async Task CreateDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDbName))
        {
            SetStatus("Enter a database name", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            await _pg.CreateDatabaseAsync(NewDbName, NewDbOwner);

            NewDbName = string.Empty;
            await LoadDatabasesAsync();
            SetStatus("Database created");
        });
    }

    [RelayCommand]
    private async Task DropDatabaseAsync(string db)
    {
        if (!Confirm($"Drop database '{db}'?\n\nAll its data is deleted permanently.", "Drop database")) return;

        await RunSafeAsync(async () =>
        {
            await _pg.DropDatabaseAsync(db);
            await LoadDatabasesAsync();
            SetStatus($"Database {db} dropped");
        });
    }

    [RelayCommand]
    private void UseDbInConnStr(string db)
    {
        ConnStringDb = db;
        SetStatus($"Selected database: {db}");
    }

    [RelayCommand]
    private async Task GrantAccessAsync()
    {
        if (string.IsNullOrWhiteSpace(GrantDb) || string.IsNullOrWhiteSpace(GrantUser))
        {
            SetStatus("Select a database and a user", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            var level = GrantLevel switch
            {
                "Owner" => PgAccessLevel.Owner,
                "Read-only" => PgAccessLevel.ReadOnly,
                _ => PgAccessLevel.ReadWrite
            };
            await _pg.GrantAsync(GrantDb!, GrantUser!, level);
            SetStatus(level == PgAccessLevel.Owner
                ? $"{GrantUser} is now OWNER of {GrantDb}"
                : $"{GrantUser} granted {GrantLevel} on {GrantDb}");
        });
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrEmpty(NewPassword))
        {
            SetStatus("Enter username and password", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            await _pg.CreateUserAsync(NewUsername, NewPassword, NewUserSuperuser);

            NewUsername = string.Empty;
            NewPassword = string.Empty;
            await LoadUsersAsync();
            SetStatus("User created");
        });
    }

    [RelayCommand]
    private async Task ChangePasswordAsync(PgUser user)
    {
        if (string.IsNullOrEmpty(NewPassword))
        {
            SetStatus("Enter a new password first", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            await _pg.ChangePasswordAsync(user.Name, NewPassword);
            NewPassword = string.Empty;
            SetStatus($"Password changed for {user.Name}");
        });
    }

    [RelayCommand]
    private async Task DropUserAsync(PgUser user)
    {
        if (!Confirm($"Drop user '{user.Name}'?", "Drop user")) return;

        await RunSafeAsync(async () =>
        {
            await _pg.DropUserAsync(user.Name);
            await LoadUsersAsync();
            SetStatus($"User {user.Name} dropped");
        });
    }

    [RelayCommand]
    private void UseUserInConnStr(PgUser user)
    {
        ConnStringUser = user.Name;
        SetStatus($"Selected user: {user.Name}");
    }

    [RelayCommand]
    private async Task GenerateConnectionStringAsync()
    {
        await RunSafeAsync(async () =>
        {
            var host = ConnHostMode switch
            {
                "Server IPv4" => PgHost.ServerIpv4,
                "Server IPv6" => PgHost.ServerIpv6,
                _ => PgHost.Localhost
            };
            int? port = int.TryParse(Port, out var p) ? p : null;
            ConnectionString = await _pg.BuildConnectionStringAsync(host, ConnStringDb, ConnStringUser, ConnStringPassword, port);
            SetStatus($"Connection string generated ({ConnHostMode})");
        });
    }

    [RelayCommand]
    private void CopyConnectionString()
    {
        if (!string.IsNullOrEmpty(ConnectionString))
        {
            System.Windows.Clipboard.SetText(ConnectionString);
            SetStatus("Copied to clipboard!");
        }
    }
}
