using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Postgres;

/// <summary>Values read from the running server with current_setting — never guessed from config files.</summary>
public sealed record PgSettings(string ListenAddresses, int Port, string HbaFile, string DataDirectory);

/// <summary>
/// Installed means a cluster exists (not just the psql client). Settings are only known while it is
/// online and this tool can query it; otherwise <see cref="Problem"/> says why they are missing.
/// </summary>
public sealed record PgStatus(bool Installed, string? Version, bool Online, PgSettings? Settings, string? Problem = null);

public enum PgAccessLevel { Owner, ReadWrite, ReadOnly }

/// <summary>Which address a generated connection string points at.</summary>
public enum PgHost { Localhost, ServerIpv4, ServerIpv6 }

/// <summary>
/// PostgreSQL operations of the PostgreSQL screen. Every query runs as the postgres superuser over the
/// local socket (see <see cref="PgCommands"/>); every change re-reads what it depends on first.
/// </summary>
public sealed class PostgresService
{
    private readonly SshService _ssh;

    public PostgresService(SshService ssh)
    {
        _ssh = ssh;
    }

    // ── Server ─────────────────────────────────────────────────────────────

    public async Task<PgStatus> GetStatusAsync()
    {
        // A cluster, not just the psql client, means the server is installed
        var clusters = (await _ssh.ExecuteAsync("pg_lsclusters -h 2>/dev/null")).Lines;
        if (clusters.Count == 0) return new PgStatus(false, null, false, null);

        var version = (await _ssh.ExecuteAsync("psql --version 2>/dev/null | head -1")).Text;
        // "16 main 5432 online postgres /var/lib/postgresql/16/main ..." — first cluster
        var online = clusters[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3)?
            .StartsWith("online", StringComparison.Ordinal) == true;

        if (!online)
            return new PgStatus(true, version, false, null, "PostgreSQL is installed but not running - restart it.");

        // Running, yet unreachable for queries (e.g. the local pg_hba rule was changed by hand):
        // still installed and online - say why the details are missing instead of failing the status
        try
        {
            return new PgStatus(true, version, true, await GetSettingsAsync());
        }
        catch (OperationException ex)
        {
            return new PgStatus(true, version, true, null, ex.Output == null ? ex.Message : $"{ex.Message} {ex.Output}");
        }
    }

    /// <summary>listen_addresses, port, hba_file and data_directory of the running server.</summary>
    public async Task<PgSettings> GetSettingsAsync()
    {
        const string notLoaded = "PostgreSQL settings could not be read - make sure it is running.";
        string[] values;
        try
        {
            values = (await QueryAsync(
                    "SELECT current_setting('listen_addresses'), current_setting('port'), " +
                    "current_setting('hba_file'), current_setting('data_directory');"))
                .Split(PgCommands.Separator);
        }
        catch (OperationException ex)
        {
            throw OperationException.Failed(notLoaded, ex.Message);
        }
        if (values.Length < 4 || !int.TryParse(values[1], out var port))
            throw OperationException.Failed(notLoaded);
        return new PgSettings(values[0], port, values[2], values[3]);
    }

    /// <summary>Checks listen_addresses and port before anything is sent; the error message, or null.</summary>
    public static string? ValidateNetwork(string listenAddresses, string port) =>
        PgCommands.ValidateListenAddresses(listenAddresses) ?? PgCommands.ValidatePort(port, out _);

    /// <summary>Checks a new pg_hba rule; returns the address as it will be written (a bare IP gets /32 or /128) or an error.</summary>
    public static (string Address, string? Error) ValidateHbaRule(string address, string user, string database, string method)
    {
        var (normalized, error) = PgCommands.NormalizeHbaAddress(address);
        error ??= PgCommands.ValidateHbaName(user.Trim(), "User")
                  ?? PgCommands.ValidateHbaName(database.Trim(), "Database")
                  ?? (PgCommands.AuthMethods.Contains(method) ? null : $"Authentication method: one of {string.Join(", ", PgCommands.AuthMethods)}.");
        return (normalized, error);
    }

    public async Task InstallAsync(Action<string> onLine)
    {
        var r = await _ssh.ExecuteStreamAsync(
            $"{Shell.AptInstall("postgresql")} && systemctl enable --now postgresql 2>&1", onLine);
        if (!r.Succeeded)
            throw OperationException.Failed($"Installing PostgreSQL failed (exit {r.ExitCode}) - see the log above.", r.Text);
    }

    public async Task RestartAsync()
    {
        var r = await _ssh.ExecuteAsync("systemctl restart postgresql 2>&1");
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }

    /// <summary>
    /// Applies listen_addresses and port, restarts, and returns the addresses now listening on the port.
    /// A value that keeps PostgreSQL from coming back is rolled back automatically.
    /// </summary>
    public async Task<List<string>> SetNetworkAsync(string listenAddresses, string port)
    {
        OperationException.ThrowIfInvalid(ValidateNetwork(listenAddresses, port));
        PgCommands.ValidatePort(port, out var portNumber);

        var settings = await GetSettingsAsync();
        var r = await _ssh.ExecuteAsync(PgCommands.SetNetwork(settings.DataDirectory, listenAddresses.Trim(), portNumber));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);

        return (await _ssh.ExecuteAsync($"ss -Htln 2>/dev/null | awk '{{print $4}}' | grep -E ':{portNumber}$'")).Lines;
    }

    // ── pg_hba.conf ────────────────────────────────────────────────────────

    public async Task<List<HbaRule>> ListHbaRulesAsync() =>
        PgCommands.ParseHbaRules(await QueryAsync(PgCommands.HbaRulesSql));

    /// <summary>Adds a host rule and reloads; returns the address as written (a bare IP gets /32 or /128).</summary>
    public async Task<string> AddHbaRuleAsync(string address, string user, string database, string method)
    {
        var (normalized, error) = ValidateHbaRule(address, user, database, method);
        OperationException.ThrowIfInvalid(error);

        var settings = await GetSettingsAsync();
        var r = await _ssh.ExecuteAsync(PgCommands.AddHbaRule(settings.HbaFile, database.Trim(), user.Trim(), normalized, method));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return normalized;
    }

    /// <summary>
    /// Removes a rule if it is still exactly where it was read from, and refuses to remove the local rule
    /// this app itself connects through.
    /// </summary>
    public async Task RemoveHbaRuleAsync(HbaRule rule)
    {
        // Line numbers shift when the file is edited elsewhere; only delete what is still there
        var current = await ListHbaRulesAsync();
        if (!current.Contains(rule))
            throw OperationException.NotFound("pg_hba.conf changed since it was loaded - reload the rules and try again.");
        if (!PgCommands.KeepsAppAccess(current.Where(r => r != rule)))
            throw OperationException.Refused(
                "This rule is how the app itself reaches PostgreSQL (local socket, user postgres, no password). " +
                "Removing it would lock the app out, so it stays.");

        var r = await _ssh.ExecuteAsync(PgCommands.RemoveHbaLine(rule.File, rule.LineNumber));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }

    // ── Users ──────────────────────────────────────────────────────────────

    public async Task<List<PgUser>> ListUsersAsync()
    {
        var output = await QueryAsync("SELECT usename, usesuper, usecreatedb FROM pg_user ORDER BY usename;");
        var users = new List<PgUser>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(PgCommands.Separator);
            if (parts.Length >= 3)
                users.Add(new PgUser(parts[0], parts[1] == "t", parts[2] == "t"));
        }
        return users;
    }

    /// <summary>
    /// Creates a login role. CREATEDB lets EF-style migrations create their database; CREATEROLE is not
    /// granted: it lets a role create other roles and is far more than an application needs.
    /// </summary>
    public async Task CreateUserAsync(string name, string password, bool superuser)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
            throw OperationException.Invalid("Enter username and password");

        var role = superuser ? "SUPERUSER" : "NOSUPERUSER";
        var ident = PgCommands.Ident(name.Trim());
        string Sql(string pwd) => $"CREATE USER {ident} WITH PASSWORD {pwd} {role} CREATEDB;";
        await ExecSqlAsync(Sql(PgCommands.Literal(password)), logSql: Sql("'***'"));
    }

    public async Task ChangePasswordAsync(string name, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw OperationException.Invalid("Enter a new password first");

        string Sql(string pwd) => $"ALTER USER {PgCommands.Ident(name)} WITH PASSWORD {pwd};";
        await ExecSqlAsync(Sql(PgCommands.Literal(password)), logSql: Sql("'***'"));
    }

    public async Task DropUserAsync(string name) =>
        await ExecSqlAsync($"DROP USER IF EXISTS {PgCommands.Ident(name)};");

    // ── Databases ──────────────────────────────────────────────────────────

    public async Task<List<string>> ListDatabasesAsync()
    {
        var output = await QueryAsync("SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Created by the postgres superuser; the owner is any existing role.</summary>
    public async Task CreateDatabaseAsync(string name, string? owner)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw OperationException.Invalid("Enter a database name");

        var ownerRole = string.IsNullOrWhiteSpace(owner) ? "postgres" : owner.Trim();
        await ExecSqlAsync($"CREATE DATABASE {PgCommands.Ident(name.Trim())} OWNER {PgCommands.Ident(ownerRole)};");
    }

    public async Task DropDatabaseAsync(string name) =>
        await ExecSqlAsync($"DROP DATABASE IF EXISTS {PgCommands.Ident(name)};");

    /// <summary>
    /// Owner hands the database over; read-write / read-only grant on schema public, its tables and
    /// sequences, including ones created later (default privileges).
    /// </summary>
    public async Task GrantAsync(string database, string user, PgAccessLevel level)
    {
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user))
            throw OperationException.Invalid("Select a database and a user");

        var db = PgCommands.Ident(database);
        var u = PgCommands.Ident(user);

        if (level == PgAccessLevel.Owner)
        {
            await ExecSqlAsync($"ALTER DATABASE {db} OWNER TO {u};");
            return;
        }

        // Read-only or Read-write: run inside the target DB so schema/table grants apply
        var readWrite = level == PgAccessLevel.ReadWrite;
        var tablePrivs = readWrite ? "SELECT, INSERT, UPDATE, DELETE" : "SELECT";
        var seqPrivs = readWrite ? "USAGE, SELECT" : "SELECT";
        var schemaPrivs = readWrite ? "USAGE, CREATE" : "USAGE";

        await ExecSqlAsync(string.Join("\n",
            $"GRANT CONNECT ON DATABASE {db} TO {u};",
            $"GRANT {schemaPrivs} ON SCHEMA public TO {u};",
            $"GRANT {tablePrivs} ON ALL TABLES IN SCHEMA public TO {u};",
            $"GRANT {seqPrivs} ON ALL SEQUENCES IN SCHEMA public TO {u};",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {tablePrivs} ON TABLES TO {u};",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT {seqPrivs} ON SEQUENCES TO {u};"), database);
    }

    // ── Connection string / raw SQL ────────────────────────────────────────

    /// <summary>An Npgsql connection string. Without a password the placeholder YOUR_PASSWORD is used.</summary>
    /// <param name="port">The server's own port when null.</param>
    public async Task<string> BuildConnectionStringAsync(PgHost host, string database, string user, string? password, int? port = null)
    {
        var address = host switch
        {
            PgHost.ServerIpv4 => (await _ssh.RunCommandAsync(
                "ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1")).Trim(),
            PgHost.ServerIpv6 => (await _ssh.RunCommandAsync(
                "ip -6 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1")).Trim(),
            _ => "localhost"
        };
        if (string.IsNullOrWhiteSpace(address))
            throw OperationException.NotFound($"No {(host == PgHost.ServerIpv6 ? "IPv6" : "IPv4")} address found on the server");

        port ??= (await GetSettingsAsync()).Port;
        var pwd = string.IsNullOrEmpty(password) ? "YOUR_PASSWORD" : password;
        return $"Host={address};Port={port};" +
               $"Database={PgCommands.ConnStringValue(database)};" +
               $"Username={PgCommands.ConnStringValue(user)};" +
               $"Password={PgCommands.ConnStringValue(pwd)}";
    }

    /// <summary>Runs SQL as postgres and returns psql's aligned output; stops at the first error.</summary>
    public async Task<string> RunSqlAsync(string sql, string? database = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw OperationException.Invalid("Enter the SQL to run.");

        var r = await _ssh.ExecuteAsync(PgCommands.Run(sql, string.IsNullOrWhiteSpace(database) ? null : database));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return r.Output.TrimEnd();
    }

    /// <summary>Runs one SQL query and returns its trimmed output; throws with psql's message on failure.</summary>
    private async Task<string> QueryAsync(string sql, string? database = null)
    {
        var r = await _ssh.ExecuteAsync(PgCommands.Query(sql, database));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return r.Output.Trim();
    }

    /// <summary>Runs SQL statements; throws with psql's message on the first error.</summary>
    private async Task ExecSqlAsync(string sql, string? database = null, string? logSql = null)
    {
        var r = await _ssh.ExecuteAsync(PgCommands.Run(sql, database),
            logSql == null ? null : PgCommands.Run(logSql, database));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }
}
