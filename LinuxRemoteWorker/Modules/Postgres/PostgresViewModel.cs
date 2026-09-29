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

    private readonly SshService _ssh;

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
    private string _hbaFile = string.Empty;
    private string _dataDirectory = string.Empty;

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
        _ssh = ssh;
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
        // A cluster, not just the psql client, means the server is installed
        var clusters = (await _ssh.ExecuteAsync("pg_lsclusters -h 2>/dev/null")).Lines;
        IsInstalled = clusters.Count > 0;
        if (!IsInstalled) return;

        Version = (await _ssh.ExecuteAsync("psql --version 2>/dev/null | head -1")).Text;
        // "16 main 5432 online postgres /var/lib/postgresql/16/main ..." — first cluster
        var online = clusters[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3)?
            .StartsWith("online", StringComparison.Ordinal) == true;
        ServiceStatus = online ? "active" : "down";
        if (!online)
        {
            SetStatus("PostgreSQL is installed but not running - press Restart.", isError: true);
            return;
        }

        await LoadConfigAsync();
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
            var r = await _ssh.ExecuteStreamAsync(
                $"{Shell.AptInstall("postgresql")} && systemctl enable --now postgresql 2>&1", Append);
            if (!r.Succeeded)
                throw new InvalidOperationException($"Installing PostgreSQL failed (exit {r.ExitCode}) - see the log above.");

            Append("\n✓ Done!");
            await LoadAllAsync();
        });

        IsInstalling = false;
    }

    /// <summary>Runs one SQL query and returns its trimmed output; throws with psql's message on failure.</summary>
    private async Task<string> QueryAsync(string sql, string? database = null)
    {
        var r = await _ssh.ExecuteAsync(PgCommands.Query(sql, database));
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
        return r.Output.Trim();
    }

    /// <summary>Runs SQL statements; throws with psql's message on the first error.</summary>
    private async Task ExecSqlAsync(string sql, string? database = null, string? logSql = null)
    {
        var r = await _ssh.ExecuteAsync(PgCommands.Run(sql, database),
            logSql == null ? null : PgCommands.Run(logSql, database));
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
    }

    private async Task LoadConfigAsync()
    {
        // The running server's own values — no guessing from config files and their comments
        var values = (await QueryAsync(
                "SELECT current_setting('listen_addresses'), current_setting('port'), " +
                "current_setting('hba_file'), current_setting('data_directory');"))
            .Split(PgCommands.Separator);
        if (values.Length < 4) return;
        ListenAddresses = values[0];
        Port = values[1];
        _hbaFile = values[2];
        _dataDirectory = values[3];
    }

    private async Task LoadHbaRulesAsync()
    {
        var rules = PgCommands.ParseHbaRules(await QueryAsync(PgCommands.HbaRulesSql));
        HbaRules.Clear();
        foreach (var rule in rules) HbaRules.Add(rule);
    }

    [RelayCommand]
    private async Task SaveListenAddressAsync()
    {
        var portError = PgCommands.ValidatePort(Port, out var port);
        var error = PgCommands.ValidateListenAddresses(ListenAddresses) ?? portError ?? NotLoadedError(_dataDirectory);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyInstalled = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Applying and restarting PostgreSQL...");
            var r = await _ssh.ExecuteAsync(PgCommands.SetNetwork(_dataDirectory, ListenAddresses.Trim(), port));
            await LoadAllAsync();
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);

            var listening = (await _ssh.ExecuteAsync($"ss -Htln 2>/dev/null | awk '{{print $4}}' | grep -E ':{port}$'")).Lines;
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
            var r = await _ssh.ExecuteAsync("systemctl restart postgresql 2>&1");
            await LoadAllAsync();
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);
            if (ServiceStatus == "active") SetStatus("PostgreSQL restarted");
        });
        IsBusyInstalled = false;
    }

    [RelayCommand]
    private async Task AddHbaRuleAsync()
    {
        var (address, error) = PgCommands.NormalizeHbaAddress(NewAllowIp);
        error ??= PgCommands.ValidateHbaName(NewAllowUser.Trim(), "User")
                  ?? PgCommands.ValidateHbaName(NewAllowDb.Trim(), "Database")
                  ?? (PgCommands.AuthMethods.Contains(NewAllowMethod) ? null : "Pick an authentication method.")
                  ?? NotLoadedError(_hbaFile);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            var r = await _ssh.ExecuteAsync(PgCommands.AddHbaRule(
                _hbaFile, NewAllowDb.Trim(), NewAllowUser.Trim(), address, NewAllowMethod));
            await LoadHbaRulesAsync();
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);

            NewAllowIp = string.Empty;
            SetStatus($"Rule added for {address} and PostgreSQL reloaded");
        });
    }

    [RelayCommand]
    private async Task RemoveHbaRuleAsync(HbaRule rule)
    {
        await RunSafeAsync(async () =>
        {
            // Line numbers shift when the file is edited elsewhere; only delete what is still there
            var current = PgCommands.ParseHbaRules(await QueryAsync(PgCommands.HbaRulesSql));
            if (!current.Contains(rule))
            {
                await LoadHbaRulesAsync();
                throw new InvalidOperationException("pg_hba.conf changed since it was loaded - the list is refreshed, try again.");
            }
            if (!PgCommands.KeepsAppAccess(current.Where(r => r != rule)))
                throw new InvalidOperationException(
                    "This rule is how the app itself reaches PostgreSQL (local socket, user postgres, no password). " +
                    "Removing it would lock the app out, so it stays.");

            var r = await _ssh.ExecuteAsync(PgCommands.RemoveHbaLine(rule.File, rule.LineNumber));
            await LoadHbaRulesAsync();
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);
            SetStatus("Rule removed");
        });
    }

    private async Task LoadUsersAsync()
    {
        var output = await QueryAsync("SELECT usename, usesuper, usecreatedb FROM pg_user ORDER BY usename;");
        Users.Clear();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(PgCommands.Separator);
            if (parts.Length >= 3)
                Users.Add(new PgUser(parts[0], parts[1] == "t", parts[2] == "t"));
        }
    }

    private async Task LoadDatabasesAsync()
    {
        var output = await QueryAsync("SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;");
        Databases.Clear();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Databases.Add(line);
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
            // Created by the postgres superuser; owner is any existing role you pick.
            var owner = string.IsNullOrWhiteSpace(NewDbOwner) ? "postgres" : NewDbOwner.Trim();
            await ExecSqlAsync($"CREATE DATABASE {PgCommands.Ident(NewDbName.Trim())} OWNER {PgCommands.Ident(owner)};");

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
            await ExecSqlAsync($"DROP DATABASE IF EXISTS {PgCommands.Ident(db)};");
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
            var db = PgCommands.Ident(GrantDb!);
            var u = PgCommands.Ident(GrantUser!);

            if (GrantLevel == "Owner")
            {
                await ExecSqlAsync($"ALTER DATABASE {db} OWNER TO {u};");
                SetStatus($"{GrantUser} is now OWNER of {GrantDb}");
                return;
            }

            // Read-only or Read-write: run inside the target DB so schema/table grants apply
            var tablePrivs = GrantLevel == "Read-write" ? "SELECT, INSERT, UPDATE, DELETE" : "SELECT";
            var seqPrivs = GrantLevel == "Read-write" ? "USAGE, SELECT" : "SELECT";
            var schemaPrivs = GrantLevel == "Read-write" ? "USAGE, CREATE" : "USAGE";

            await ExecSqlAsync(string.Join("\n",
                $"GRANT CONNECT ON DATABASE {db} TO {u};",
                $"GRANT {schemaPrivs} ON SCHEMA public TO {u};",
                $"GRANT {tablePrivs} ON ALL TABLES IN SCHEMA public TO {u};",
                $"GRANT {seqPrivs} ON ALL SEQUENCES IN SCHEMA public TO {u};",
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {tablePrivs} ON TABLES TO {u};",
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {seqPrivs} ON SEQUENCES TO {u};"), GrantDb);
            SetStatus($"{GrantUser} granted {GrantLevel} on {GrantDb}");
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
            // CREATEDB lets EF-style migrations create their database; CREATEROLE is not granted:
            // it lets a role create other roles and is far more than an application needs.
            var role = NewUserSuperuser ? "SUPERUSER" : "NOSUPERUSER";
            var name = PgCommands.Ident(NewUsername.Trim());
            string Sql(string password) => $"CREATE USER {name} WITH PASSWORD {password} {role} CREATEDB;";
            await ExecSqlAsync(Sql(PgCommands.Literal(NewPassword)), logSql: Sql("'***'"));

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
            string Sql(string password) => $"ALTER USER {PgCommands.Ident(user.Name)} WITH PASSWORD {password};";
            await ExecSqlAsync(Sql(PgCommands.Literal(NewPassword)), logSql: Sql("'***'"));
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
            await ExecSqlAsync($"DROP USER IF EXISTS {PgCommands.Ident(user.Name)};");
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
            string host = ConnHostMode switch
            {
                "localhost" => "localhost",
                "Server IPv4" => (await _ssh.RunCommandAsync(
                    "ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1")).Trim(),
                "Server IPv6" => (await _ssh.RunCommandAsync(
                    "ip -6 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1")).Trim(),
                _ => _ssh.Host ?? "localhost"
            };

            if (string.IsNullOrWhiteSpace(host))
            {
                SetStatus($"No address found for '{ConnHostMode}' on the server", isError: true);
                return;
            }

            var password = string.IsNullOrEmpty(ConnStringPassword) ? "YOUR_PASSWORD" : ConnStringPassword;
            ConnectionString = $"Host={host};Port={Port};" +
                               $"Database={PgCommands.ConnStringValue(ConnStringDb)};" +
                               $"Username={PgCommands.ConnStringValue(ConnStringUser)};" +
                               $"Password={PgCommands.ConnStringValue(password)}";
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

    // Paths come from the running server; without them there is nothing safe to edit
    private static string? NotLoadedError(string path) =>
        path.Length == 0 ? "PostgreSQL settings are not loaded - make sure it is running and press Refresh." : null;
}
