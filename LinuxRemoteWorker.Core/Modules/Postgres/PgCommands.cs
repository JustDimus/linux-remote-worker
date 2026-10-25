using System.Net;
using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Postgres;

/// <summary>
/// Builds the server commands of the PostgreSQL screen. SQL reaches psql on stdin through a quoted
/// heredoc, so passwords and names never pass through shell expansion ($, `, " stay literal), and
/// every identifier and literal is escaped the SQL way.
/// </summary>
public static class PgCommands
{
    /// <summary>Column separator of <see cref="Query"/> output.</summary>
    public const char Separator = '|';

    private static readonly Regex HbaName = new(@"^[A-Za-z0-9_.$+@-]+(,[A-Za-z0-9_.$+@-]+)*$");
    private static readonly Regex ListenAddresses = new(@"^[A-Za-z0-9.:*, -]+$");

    public static readonly IReadOnlyList<string> AuthMethods = ["scram-sha-256", "md5", "trust", "reject", "peer"];

    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Runs SQL as the postgres superuser; the first error stops it with a non-zero exit code.</summary>
    public static string Run(string sql, string? database = null) =>
        Shell.Heredoc($"cd / && runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1{Db(database)} 2>&1", sql);

    /// <summary>Like <see cref="Run"/>, but prints bare rows (no headers), columns separated by <see cref="Separator"/>.</summary>
    public static string Query(string sql, string? database = null) =>
        Shell.Heredoc($"cd / && runuser -u postgres -- psql -X -q -t -A -F '{Separator}' -v ON_ERROR_STOP=1{Db(database)}", sql);

    private static string Db(string? database) => database == null ? "" : " -d " + Shell.Quote(database);

    /// <summary>
    /// Every rule of the running cluster's pg_hba.conf, as PostgreSQL itself parses it. file_name only
    /// exists from PostgreSQL 16 (included files); to_jsonb reads it when present, else hba_file.
    /// </summary>
    public const string HbaRulesSql =
        """
        SELECT r.line_number, r.type, array_to_string(r.database, ','), array_to_string(r.user_name, ','),
               coalesce(r.address, ''), coalesce(r.netmask, ''), coalesce(r.auth_method, ''), coalesce(r.error, ''),
               coalesce(to_jsonb(r) ->> 'file_name', current_setting('hba_file'))
        FROM pg_hba_file_rules r ORDER BY 9, r.line_number;
        """;

    public static List<HbaRule> ParseHbaRules(string output)
    {
        var rules = new List<HbaRule>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(Separator);
            if (f.Length < 9 || !int.TryParse(f[0], out var number)) continue;
            var address = f[5].Length > 0 ? $"{f[4]}/{PrefixLength(f[5])}" : f[4];
            var method = f[7].Length > 0 ? $"⚠ {f[7]}" : f[6];
            rules.Add(new HbaRule(number, f[1], f[2], f[3], address, method) { File = f[8] });
        }
        return rules;
    }

    // "255.255.255.0" → "24"; anything unexpected is shown as it is
    private static string PrefixLength(string netmask) =>
        IPAddress.TryParse(netmask, out var mask)
            ? mask.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b)).ToString()
            : netmask;

    /// <summary>
    /// Appends a host rule to pg_hba.conf and reloads. PostgreSQL re-parses the file first; if the new
    /// line has an error the previous file is restored (exit 2) and nothing is reloaded.
    /// </summary>
    public static string AddHbaRule(string hbaFile, string database, string user, string address, string method)
    {
        var hba = Shell.Quote(hbaFile);
        var rule = $"host    {database}    {user}    {address}    {method}";
        return $$"""
            exec 2>&1
            bak=$(mktemp) || exit 1
            trap 'rm -f "$bak"' EXIT
            cp -p {{hba}} "$bak" || exit 1
            before=$(wc -l < "$bak")
            printf '%s\n' {{Shell.Quote(rule)}} >> {{hba}} || exit 1
            if ! errors=$(cd / && runuser -u postgres -- psql -XtA -c "SELECT line_number || ': ' || error FROM pg_hba_file_rules WHERE error IS NOT NULL AND line_number > $before" 2>&1); then
              cp -p "$bak" {{hba}}
              echo "Could not check the new rule, pg_hba.conf is unchanged:"
              echo "$errors"
              exit 2
            fi
            if [ -n "$errors" ]; then
              cp -p "$bak" {{hba}}
              echo "PostgreSQL rejected the rule, pg_hba.conf is unchanged:"
              echo "$errors"
              exit 2
            fi
            cd / && runuser -u postgres -- psql -XtAqc 'SELECT pg_reload_conf()' >/dev/null
            """;
    }

    /// <summary>
    /// True when a rule still lets this app in: it runs every query as "runuser -u postgres psql" over the
    /// local socket, which needs a local rule for user postgres that asks for no password.
    /// </summary>
    public static bool KeepsAppAccess(IEnumerable<HbaRule> rules) =>
        rules.Any(r => r.Type == "local" &&
                       r.Database.Split(',').Any(d => d is "all" or "postgres") &&
                       r.User.Split(',').Any(u => u is "all" or "postgres") &&
                       r.Method is "peer" or "trust" or "ident");

    /// <summary>Deletes one line of pg_hba.conf (or the included file the rule is in) and reloads.</summary>
    public static string RemoveHbaLine(string hbaFile, int lineNumber) =>
        $"exec 2>&1\nsed -i '{lineNumber}d' {Shell.Quote(hbaFile)} || exit 1\n" +
        "cd / && runuser -u postgres -- psql -XtAqc 'SELECT pg_reload_conf()' >/dev/null";

    /// <summary>
    /// Sets listen_addresses and port with ALTER SYSTEM and restarts. If the server does not accept
    /// connections again within 15 s, postgresql.auto.conf is restored and it is restarted once more
    /// (exit 2), so a bad value never leaves the database down.
    /// </summary>
    public static string SetNetwork(string dataDirectory, string listenAddresses, int port)
    {
        var auto = Shell.Quote($"{dataDirectory.TrimEnd('/')}/postgresql.auto.conf");
        var sql = $"ALTER SYSTEM SET listen_addresses = {Literal(listenAddresses)};\nALTER SYSTEM SET port = {port};";
        return $$"""
            exec 2>&1
            bak=$(mktemp) || exit 1
            trap 'rm -f "$bak"' EXIT
            cp -p {{auto}} "$bak" || exit 1
            {{Run(sql)}}
            # ALTER SYSTEM is not transactional: undo a half-applied pair
            [ $? -eq 0 ] || { cp -p "$bak" {{auto}}; exit 1; }
            systemctl restart postgresql
            for i in $(seq 1 15); do
              if runuser -u postgres -- pg_isready -q -p {{port}}; then exit 0; fi
              sleep 1
            done
            cp -p "$bak" {{auto}}
            systemctl restart postgresql
            echo "PostgreSQL did not accept connections with the new settings - the previous ones were restored."
            exit 2
            """;
    }

    /// <summary>Normalises a pg_hba address: a bare IP gets /32 or /128, because pg_hba.conf reads it as a host name.</summary>
    public static (string Address, string? Error) NormalizeHbaAddress(string input)
    {
        var a = input.Trim();
        if (a is "all" or "samehost" or "samenet") return (a, null);

        if (!NetAddress.TryParseCidr(a, out var ip, out var prefix))
            return (a, "Address must be an IP address, a CIDR range such as 10.0.0.0/24, or all.");
        return prefix == null ? ($"{a}/{NetAddress.MaxPrefix(ip)}", null) : (a, null);
    }

    public static string? ValidateHbaName(string value, string what) =>
        HbaName.IsMatch(value) ? null : $"{what}: 'all' or comma-separated names (letters, digits, _ . $ + @ -).";

    public static string? ValidateListenAddresses(string value) =>
        ListenAddresses.IsMatch(value.Trim())
            ? null
            : "Listen addresses: '*', 'localhost', or comma-separated IP addresses / host names.";

    public static string? ValidatePort(string value, out int port) =>
        int.TryParse(value.Trim(), out port) && port is > 0 and < 65536 ? null : "Port must be a number between 1 and 65535.";

    /// <summary>Quotes a connection-string value when it contains characters that would break the string.</summary>
    public static string ConnStringValue(string value) =>
        value.IndexOfAny([';', '\'', '"', '=']) >= 0 || value != value.Trim()
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
