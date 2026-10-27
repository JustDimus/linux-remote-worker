using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Session;

/// <summary>A server to connect to. Passphrase and password are kept in memory only and never printed.</summary>
public sealed record ConnectionTarget(
    string Host,
    string Username,
    string KeyPath,
    string? Passphrase,
    string? ProfileName,
    SshAuthMethod Method = SshAuthMethod.Key,
    string? Password = null)
{
    public string Display => ProfileName == null ? $"{Username}@{Host}" : $"{Username}@{Host} ({ProfileName})";

    public SshCredentials Credentials => Method == SshAuthMethod.Password
        ? SshCredentials.WithPassword(Host, Username, Password)
        : SshCredentials.WithKey(Host, Username, KeyPath, Passphrase);

    // The generated record ToString would print the passphrase and the password
    public override string ToString() => Display;
}

/// <summary>How the user picked a server: global options, MCP connection_use arguments, or nothing (defaults).</summary>
public sealed record ConnectionOptions
{
    public string? Profile { get; init; }
    public string? Host { get; init; }
    public string? User { get; init; }
    public string? Key { get; init; }
    public string? PassphraseEnv { get; init; }
    public string? PassphraseFile { get; init; }
    public string? PasswordEnv { get; init; }
    public string? PasswordFile { get; init; }
}

/// <summary>
/// Picks the server, in this order: --host (or LRW_HOST) with --key or a password, --profile (or LRW_PROFILE),
/// the default set with `lrw profiles use`, and finally the only saved profile when there is just one.
/// A password comes from --password-file, --password-env or LRW_PASSWORD, else from the profile (saved
/// encrypted by the desktop app or `profiles add --password`); without one the interactive CLI asks.
/// </summary>
public static class TargetResolver
{
    public const string PassphraseVariable = "LRW_PASSPHRASE";
    public const string PasswordVariable = "LRW_PASSWORD";

    public static ConnectionTarget Resolve(ConnectionOptions options) =>
        Resolve(options, Environment.GetEnvironmentVariable, () => new ProfileService().LoadStrict(), () => CliSettings.Load().DefaultProfile);

    /// <summary>The same, with the environment, the saved profiles and the default profile supplied (for tests).</summary>
    public static ConnectionTarget Resolve(ConnectionOptions options, Func<string, string?> environment,
        Func<List<ConnectionProfile>> loadProfiles, Func<string?> defaultProfile)
    {
        string? Env(string name) => Value(environment(name));

        var passphrase = ReadSecret(options.PassphraseFile, options.PassphraseEnv, PassphraseVariable, "--passphrase", Env);
        var password = ReadSecret(options.PasswordFile, options.PasswordEnv, PasswordVariable, "--password", Env);

        var host = Value(options.Host) ?? Env("LRW_HOST");
        if (host != null)
        {
            var user = Value(options.User) ?? Env("LRW_USER") ?? "root";
            var key = Value(options.Key) ?? Env("LRW_KEY");
            if (key != null)
                return new ConnectionTarget(host.Trim(), user, Path.GetFullPath(key), passphrase, null);
            if (password != null)
                return new ConnectionTarget(host.Trim(), user, "", null, null, SshAuthMethod.Password, password);
            throw new UsageException(
                $"--host needs --key <path to the private key> (or LRW_KEY), or a password in {PasswordVariable} " +
                "(or --password-env <VAR> / --password-file <path>).");
        }

        var profiles = loadProfiles();
        var name = Value(options.Profile) ?? Env("LRW_PROFILE") ?? defaultProfile();
        ConnectionProfile profile;
        if (name != null)
        {
            profile = FindProfile(profiles, name)
                      ?? throw new UsageException($"No saved profile '{name}'. {Saved(profiles)}", "profiles");
        }
        else if (profiles.Count == 1)
        {
            profile = profiles[0];
        }
        else
        {
            throw new UsageException(profiles.Count == 0
                ? "No server selected and no saved profiles. Save one with " +
                  "`lrw profiles add <name> --host <ip> --key <private key path>`, or pass --host and --key."
                : $"No server selected. Pass --profile <name>, or set a default with `lrw profiles use <name>`. {Saved(profiles)}",
                "profiles");
        }

        var username = Value(options.User) ?? profile.Username;
        return profile.AuthMethod == SshAuthMethod.Password
            ? new ConnectionTarget(profile.Host, username, "", null, profile.Name, SshAuthMethod.Password,
                password ?? PasswordProtector.Unprotect(profile.ProtectedPassword))
            : new ConnectionTarget(profile.Host, username, profile.PrivateKeyPath, passphrase, profile.Name);
    }

    public static ConnectionProfile? FindProfile(IEnumerable<ConnectionProfile> profiles, string nameOrId) =>
        profiles.FirstOrDefault(p => p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? profiles.FirstOrDefault(p => p.Id.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    private static string Saved(IReadOnlyCollection<ConnectionProfile> profiles) =>
        profiles.Count == 0 ? "There are no saved profiles." : $"Saved profiles: {string.Join(", ", profiles.Select(p => p.Name))}.";

    // --x-file wins over --x-env, which wins over the default variable
    private static string? ReadSecret(string? file, string? variable, string defaultVariable, string option, Func<string, string?> env)
    {
        if (file is { } path)
        {
            if (!File.Exists(path)) throw new UsageException($"File not found ({option}-file): {path}");
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }

        if (variable is { } name)
            return env(name) ?? throw new UsageException($"Environment variable {name} ({option}-env) is not set.");

        return env(defaultVariable);
    }

    // An empty option counts as not given, so "--key ''" fails with a clear message instead of an empty path
    private static string? Value(string? option) => string.IsNullOrWhiteSpace(option) ? null : option;
}
