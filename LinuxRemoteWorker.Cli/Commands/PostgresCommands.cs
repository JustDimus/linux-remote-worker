using System.Security.Cryptography;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Postgres;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("pg", "PostgreSQL: install, network settings, pg_hba.conf, users, databases, grants, connection strings")]
public sealed class PostgresCommands : CommandSet
{
    // Letters and digits only: safe in any connection string, shell or config file without quoting
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public PostgresCommands(CommandContext context) : base(context) { }

    private async Task<PostgresService> PgAsync() => new(await SshAsync());

    [Command("status", "Whether PostgreSQL is installed and running, its version, listen_addresses, port, and the paths of pg_hba.conf and the data directory.",
        ReadOnly = true)]
    public async Task<PgStatus> Status() => await (await PgAsync()).GetStatusAsync();

    [Command("install", "Install the PostgreSQL server (apt) and enable it at boot.", Streams = true, Idempotent = true)]
    public async Task<Outcome> Install()
    {
        var pg = await PgAsync();
        await pg.InstallAsync(Lines);
        return new Outcome("PostgreSQL installed and running") { Data = await pg.GetStatusAsync() };
    }

    [Command("restart", "Restart PostgreSQL.")]
    public async Task<Outcome> Restart()
    {
        var pg = await PgAsync();
        await pg.RestartAsync();
        return new Outcome("PostgreSQL restarted") { Data = await pg.GetStatusAsync() };
    }

    [Command("network", "Set listen_addresses and port (ALTER SYSTEM + restart). If PostgreSQL does not come back within 15 s the previous values are restored.",
        Example = "pg network --listen '*' --port 5432")]
    public async Task<Outcome> Network(
        [Arg("'*' for every interface, 'localhost', or comma-separated addresses")] string listen,
        [Arg("TCP port")] int port = 5432)
    {
        var listening = await (await PgAsync()).SetNetworkAsync(listen, port.ToString());
        return new Outcome($"Saved & restarted. Listening on: {string.Join(", ", listening)}") { Data = new { listening } };
    }

    // ── pg_hba.conf ────────────────────────────────────────────────────────

    [Command("hba list", "The rules of pg_hba.conf as PostgreSQL itself parsed them (file, line, type, database, user, address, method). A method starting with ⚠ is a line PostgreSQL rejected.",
        ReadOnly = true)]
    public async Task<List<HbaRule>> HbaList() => await (await PgAsync()).ListHbaRulesAsync();

    [Command("hba add", "Allow a client address to connect over TCP, then reload. A bare IP gets /32 or /128; a rule PostgreSQL rejects is rolled back.",
        Example = "pg hba add --address 10.0.0.0/24 --db shop --user shop")]
    public async Task<Outcome> HbaAdd(
        [Arg("Client IP, CIDR range such as 10.0.0.0/24, 'all', 'samehost' or 'samenet'")] string address,
        [Arg("Database name(s), comma-separated, or 'all'")] string db = "all",
        [Arg("User name(s), comma-separated, or 'all'")] string user = "all",
        [Arg("Authentication method", Choices = new[] { "scram-sha-256", "md5", "trust", "reject", "peer" })] string method = "scram-sha-256")
    {
        var written = await (await PgAsync()).AddHbaRuleAsync(address, user, db, method);
        return new Outcome($"Rule added for {written} and PostgreSQL reloaded");
    }

    [Command("hba remove", "Remove one pg_hba.conf rule by its line number (see hba list) and reload. The local rule this tool itself connects through cannot be removed.",
        Destructive = true)]
    public async Task<Outcome> HbaRemove(
        [Arg("Line number of the rule, from hba list")] int line,
        [Arg("File of the rule, from hba list (only needed when included files are used)")] string? file = null)
    {
        var pg = await PgAsync();
        var matches = (await pg.ListHbaRulesAsync())
            .Where(r => r.LineNumber == line && (file == null || r.File == file))
            .ToList();
        var rule = matches.Count switch
        {
            0 => throw OperationException.NotFound($"No pg_hba.conf rule on line {line}" + (file == null ? "." : $" of {file}.")),
            1 => matches[0],
            _ => throw OperationException.Invalid($"Line {line} exists in several files ({string.Join(", ", matches.Select(r => r.File))}) - pass --file.")
        };

        await pg.RemoveHbaRuleAsync(rule);
        return new Outcome($"Rule on line {line} removed and PostgreSQL reloaded") { Data = rule };
    }

    // ── Users ──────────────────────────────────────────────────────────────

    [Command("users list", "Database roles that can log in, with superuser and createdb flags.", ReadOnly = true)]
    public async Task<List<PgUser>> UsersList() => await (await PgAsync()).ListUsersAsync();

    [Command("users create", "Create a login role with CREATEDB (enough for EF migrations) and without CREATEROLE. Pass --password, or --generate-password to get a strong one back.",
        Example = "pg users create --name shop --generate-password")]
    public async Task<Outcome> UsersCreate(
        [Arg("Role name")] string name,
        [Arg("Password", Secret = true)] string? password = null,
        [Arg("Generate a random 24-character password and return it")] bool generatePassword = false,
        [Arg("Make the role a superuser (rarely what an application needs)")] bool superuser = false)
    {
        var pwd = PickPassword(password, generatePassword);
        await (await PgAsync()).CreateUserAsync(name, pwd, superuser);
        return new Outcome($"User {name} created")
        {
            Data = new { name, password = generatePassword ? pwd : null, superuser },
            Warning = generatePassword ? "The generated password is shown only now - store it (e.g. in the app's configuration)." : null
        };
    }

    [Command("users password", "Set a new password for a role.")]
    public async Task<Outcome> UsersPassword(
        [Arg("Role name")] string name,
        [Arg("New password", Secret = true)] string? password = null,
        [Arg("Generate a random 24-character password and return it")] bool generatePassword = false)
    {
        var pwd = PickPassword(password, generatePassword);
        await (await PgAsync()).ChangePasswordAsync(name, pwd);
        return new Outcome($"Password changed for {name}")
        {
            Data = generatePassword ? new { name, password = pwd } : null,
            Warning = generatePassword ? "The generated password is shown only now - store it." : null
        };
    }

    [Command("users drop", "Drop a role.", Destructive = true)]
    public async Task<Outcome> UsersDrop([Arg("Role name")] string name)
    {
        await (await PgAsync()).DropUserAsync(name);
        return new Outcome($"User {name} dropped");
    }

    // ── Databases ──────────────────────────────────────────────────────────

    [Command("db list", "Databases (templates excluded).", ReadOnly = true)]
    public async Task<List<string>> DbList() => await (await PgAsync()).ListDatabasesAsync();

    [Command("db create", "Create a database owned by an existing role.", Example = "pg db create --name shop --owner shop")]
    public async Task<Outcome> DbCreate(
        [Arg("Database name")] string name,
        [Arg("Owner role")] string owner = "postgres")
    {
        await (await PgAsync()).CreateDatabaseAsync(name, owner);
        return new Outcome($"Database {name} created (owner {owner})");
    }

    [Command("db drop", "Drop a database and all of its data.", Destructive = true)]
    public async Task<Outcome> DbDrop([Arg("Database name")] string name)
    {
        await (await PgAsync()).DropDatabaseAsync(name);
        return new Outcome($"Database {name} dropped");
    }

    [Command("grant", "Give a role access to a database: owner, read-write or read-only (schema public, current and future tables and sequences).")]
    public async Task<Outcome> Grant(
        [Arg("Database name")] string db,
        [Arg("Role name")] string user,
        [Arg("Access level")] PgAccessLevel level = PgAccessLevel.ReadWrite)
    {
        await (await PgAsync()).GrantAsync(db, user, level);
        return new Outcome(level == PgAccessLevel.Owner
            ? $"{user} is now owner of {db}"
            : $"{user} granted {CommandCatalog.Kebab(level.ToString())} on {db}");
    }

    [Command("connstr", "Build an Npgsql connection string for the server. Without --password the placeholder YOUR_PASSWORD is used.",
        ReadOnly = true)]
    public async Task<object> ConnectionString(
        [Arg("Database name")] string db,
        [Arg("Role name")] string user,
        [Arg("Password to put in the string", Secret = true)] string? password = null,
        [Arg("Address the app will use: localhost (app on the same server) or the server's public IPv4/IPv6")] PgHost address = PgHost.Localhost)
    {
        var text = await (await PgAsync()).BuildConnectionStringAsync(address, db, user, password);
        return new { connectionString = text };
    }

    [Command("sql", "Run SQL as the postgres superuser and return psql's output; stops at the first error. Use the dedicated commands where one exists.",
        Unrestricted = true, Example = "pg sql --db shop --sql \"SELECT count(*) FROM orders\"")]
    public async Task<object> Sql(
        [Arg("SQL statements", AllowFile = true)] string sql,
        [Arg("Database to run in (default: postgres)")] string? db = null)
    {
        var output = await (await PgAsync()).RunSqlAsync(sql, db);
        return new { database = db ?? "postgres", output };
    }

    private static string PickPassword(string? password, bool generate)
    {
        if (generate && !string.IsNullOrEmpty(password))
            throw OperationException.Invalid("Pass either --password or --generate-password, not both.");
        if (generate) return RandomNumberGenerator.GetString(PasswordAlphabet, 24);
        return !string.IsNullOrEmpty(password)
            ? password
            : throw OperationException.Invalid("Pass --password (or env:VAR / @file), or --generate-password.");
    }
}
