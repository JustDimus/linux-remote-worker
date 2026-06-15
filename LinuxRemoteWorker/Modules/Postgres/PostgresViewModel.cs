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

    // Config
    [ObservableProperty] private string _listenAddresses = "*";
    [ObservableProperty] private string _port = "5432";
    [ObservableProperty] private string _configLog = string.Empty;

    // pg_hba entries
    [ObservableProperty] private string _newAllowIp = string.Empty;
    [ObservableProperty] private string _newAllowUser = "all";
    [ObservableProperty] private string _newAllowDb = "all";
    [ObservableProperty] private string _newAllowMethod = "scram-sha-256";
    public List<string> AuthMethods { get; } = ["scram-sha-256", "md5", "trust", "reject", "peer"];
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
        await RunSafeAsync(async () =>
        {
            var which = await _ssh.RunCommandAsync("which psql 2>/dev/null || echo ''");
            IsInstalled = !string.IsNullOrWhiteSpace(which);
            IsBusyInstalled = false;

            if (IsInstalled)
            {
                Version = await _ssh.RunCommandAsync("psql --version 2>/dev/null | head -1");
                ServiceStatus = await _ssh.RunCommandAsync("systemctl is-active postgresql 2>/dev/null || echo 'unknown'");
                await LoadConfigAsync();
                await LoadHbaRulesAsync();
                await LoadUsersAsync();
                await LoadDatabasesAsync();
            }
        });
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        InstallLog = string.Empty;

        await RunSafeAsync(async () =>
        {
            void Append(string line)
            {
                InstallLog += line + "\n";
            }

            Append("→ Updating apt...");
            await _ssh.RunCommandStreamAsync("apt-get update -y", Append);

            Append("\n→ Installing postgresql...");
            await _ssh.RunCommandStreamAsync("DEBIAN_FRONTEND=noninteractive apt-get install -y postgresql", Append);

            Append("\n→ Starting service...");
            await _ssh.RunCommandStreamAsync("systemctl enable postgresql && systemctl start postgresql", Append);

            Append("\n✓ Done!");
            await RefreshAsync();
        });

        IsInstalling = false;
    }

    private async Task LoadConfigAsync()
    {
        var conf = await _ssh.RunCommandAsync(
            "grep -E '^listen_addresses|^port' /etc/postgresql/*/main/postgresql.conf 2>/dev/null | head -5");

        foreach (var line in conf.Split('\n'))
        {
            if (line.Contains("listen_addresses"))
                ListenAddresses = line.Split('=').LastOrDefault()?.Trim().Trim('\'') ?? "*";
            if (line.Contains("port"))
                Port = line.Split('=').LastOrDefault()?.Trim() ?? "5432";
        }
    }

    private async Task LoadHbaRulesAsync()
    {
        HbaRules.Clear();
        var hba = await _ssh.RunCommandAsync(
            "grep -v '^#' /etc/postgresql/*/main/pg_hba.conf 2>/dev/null | grep -v '^$'");
        foreach (var line in hba.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            HbaRules.Add(new HbaRule(
                Type:     parts.ElementAtOrDefault(0) ?? "",
                Database: parts.ElementAtOrDefault(1) ?? "",
                User:     parts.ElementAtOrDefault(2) ?? "",
                Address:  parts.ElementAtOrDefault(3) ?? "",
                Method:   parts.ElementAtOrDefault(4) ?? "",
                Raw:      line.Trim()
            ));
        }
    }

    [RelayCommand]
    private async Task SaveListenAddressAsync()
    {
        await RunSafeAsync(async () =>
        {
            var confPath = await _ssh.RunCommandAsync("ls /etc/postgresql/*/main/postgresql.conf | head -1");
            confPath = confPath.Trim();

            await _ssh.RunCommandAsync(
                $"sed -i \"s/^#*listen_addresses.*/listen_addresses = '{ListenAddresses}'/\" {confPath}");
            await _ssh.RunCommandAsync(
                $"sed -i \"s/^#*port.*/port = {Port}/\" {confPath}");

            // listen_addresses requires full restart, not just reload
            await _ssh.RunCommandAsync("systemctl restart postgresql");

            var listening = await _ssh.RunCommandAsync("ss -tlnp | grep 5432");
            ServiceStatus = await _ssh.RunCommandAsync("systemctl is-active postgresql");
            SetStatus($"Saved & restarted. Listening: {listening.Trim()}");
        });
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        IsBusyInstalled = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Restarting PostgreSQL...");
            await _ssh.RunCommandAsync("systemctl restart postgresql");
            ServiceStatus = await _ssh.RunCommandAsync("systemctl is-active postgresql");
            SetStatus("PostgreSQL restarted");
        });
        IsBusyInstalled = false;
    }

    [RelayCommand]
    private async Task AddHbaRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAllowIp)) return;

        await RunSafeAsync(async () =>
        {
            var hbaPath = await _ssh.RunCommandAsync("ls /etc/postgresql/*/main/pg_hba.conf | head -1");
            hbaPath = hbaPath.Trim();

            var rule = $"host    {NewAllowDb}    {NewAllowUser}    {NewAllowIp}    {NewAllowMethod}";
            await _ssh.RunCommandAsync($"echo '{rule}' >> {hbaPath}");
            await _ssh.RunCommandAsync("systemctl reload postgresql 2>/dev/null || systemctl restart postgresql");

            NewAllowIp = string.Empty;
            await LoadHbaRulesAsync();
            SetStatus("Rule added and PostgreSQL reloaded");
        });
    }

    [RelayCommand]
    private async Task RemoveHbaRuleAsync(HbaRule rule)
    {
        await RunSafeAsync(async () =>
        {
            var hbaPath = await _ssh.RunCommandAsync("ls /etc/postgresql/*/main/pg_hba.conf | head -1");
            hbaPath = hbaPath.Trim();

            var escaped = rule.Raw.Replace("/", "\\/").Replace(".", "\\.").Replace("*", "\\*").Replace("[", "\\[");
            await _ssh.RunCommandAsync($"sed -i '/{escaped}/d' {hbaPath}");
            await _ssh.RunCommandAsync("systemctl reload postgresql 2>/dev/null || systemctl restart postgresql");

            await LoadHbaRulesAsync();
            SetStatus("Rule removed");
        });
    }

    private async Task LoadUsersAsync()
    {
        Users.Clear();
        var output = await _ssh.RunCommandAsync(
            "sudo -u postgres psql -t -c \"SELECT usename, usesuper, usecreatedb FROM pg_user ORDER BY usename;\" 2>/dev/null");
        foreach (var line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Split('|');
            if (parts.Length >= 3)
                Users.Add(new PgUser(parts[0].Trim(), parts[1].Trim() == "t", parts[2].Trim() == "t"));
        }
    }

    private async Task LoadDatabasesAsync()
    {
        Databases.Clear();
        var output = await _ssh.RunCommandAsync(
            "sudo -u postgres psql -t -c \"SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;\" 2>/dev/null");
        foreach (var line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            Databases.Add(line.Trim());
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
            var sql = $"CREATE DATABASE \\\"{NewDbName.Trim()}\\\" OWNER \\\"{owner}\\\";";
            var result = await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");

            if (result.Contains("ERROR"))
                throw new Exception(result);

            NewDbName = string.Empty;
            await LoadDatabasesAsync();
            SetStatus("Database created");
        });
    }

    [RelayCommand]
    private async Task DropDatabaseAsync(string db)
    {
        await RunSafeAsync(async () =>
        {
            var sql = $"DROP DATABASE IF EXISTS \\\"{db}\\\";";
            var result = await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");
            if (result.Contains("ERROR"))
                throw new Exception(result);
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
            var db = GrantDb!;
            var u = GrantUser!;

            if (GrantLevel == "Owner")
            {
                var sql = $"ALTER DATABASE \\\"{db}\\\" OWNER TO \\\"{u}\\\";";
                var r = await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");
                if (r.Contains("ERROR")) throw new Exception(r);
                SetStatus($"{u} is now OWNER of {db}");
                return;
            }

            // Read-only or Read-write: run inside the target DB so schema/table grants apply
            string tablePrivs = GrantLevel == "Read-write"
                ? "SELECT, INSERT, UPDATE, DELETE"
                : "SELECT";
            string seqPrivs = GrantLevel == "Read-write" ? "USAGE, SELECT" : "SELECT";
            string schemaPrivs = GrantLevel == "Read-write" ? "USAGE, CREATE" : "USAGE";

            var statements = string.Join(" ", new[]
            {
                $"GRANT CONNECT ON DATABASE \\\"{db}\\\" TO \\\"{u}\\\";",
                $"GRANT {schemaPrivs} ON SCHEMA public TO \\\"{u}\\\";",
                $"GRANT {tablePrivs} ON ALL TABLES IN SCHEMA public TO \\\"{u}\\\";",
                $"GRANT {seqPrivs} ON ALL SEQUENCES IN SCHEMA public TO \\\"{u}\\\";",
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {tablePrivs} ON TABLES TO \\\"{u}\\\";",
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {seqPrivs} ON SEQUENCES TO \\\"{u}\\\";"
            });

            var result = await _ssh.RunCommandAsync($"sudo -u postgres psql -d \"{db}\" -c \"{statements}\" 2>&1");
            if (result.Contains("ERROR")) throw new Exception(result);
            SetStatus($"{u} granted {GrantLevel} on {db}");
        });
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            SetStatus("Enter username and password", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            var role = NewUserSuperuser ? "SUPERUSER" : "NOSUPERUSER";
            var sql = $"CREATE USER \\\"{NewUsername}\\\" WITH PASSWORD '{NewPassword}' {role} CREATEROLE CREATEDB;";
            var result = await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");

            if (result.Contains("ERROR"))
                throw new Exception(result);

            NewUsername = string.Empty;
            NewPassword = string.Empty;
            await LoadUsersAsync();
            SetStatus("User created");
        });
    }

    [RelayCommand]
    private async Task ChangePasswordAsync(PgUser user)
    {
        if (string.IsNullOrWhiteSpace(NewPassword))
        {
            SetStatus("Enter a new password first", isError: true);
            return;
        }

        await RunSafeAsync(async () =>
        {
            var sql = $"ALTER USER \\\"{user.Name}\\\" WITH PASSWORD '{NewPassword}';";
            await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");
            NewPassword = string.Empty;
            SetStatus($"Password changed for {user.Name}");
        });
    }

    [RelayCommand]
    private async Task DropUserAsync(PgUser user)
    {
        await RunSafeAsync(async () =>
        {
            var sql = $"DROP USER IF EXISTS \\\"{user.Name}\\\";";
            var result = await _ssh.RunCommandAsync($"sudo -u postgres psql -c \"{sql}\" 2>&1");
            if (result.Contains("ERROR"))
                throw new Exception(result);
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

            var password = string.IsNullOrWhiteSpace(ConnStringPassword) ? "YOUR_PASSWORD" : ConnStringPassword;
            ConnectionString = $"Host={host};Port={Port};Database={ConnStringDb};Username={ConnStringUser};Password={password}";
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
