using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Session;

/// <summary>A server to connect to. The passphrase is kept in memory only and never printed.</summary>
public sealed record ConnectionTarget(string Host, string Username, string KeyPath, string? Passphrase, string? ProfileName)
{
    public string Display => ProfileName == null ? $"{Username}@{Host}" : $"{Username}@{Host} ({ProfileName})";

    // The generated record ToString would print the passphrase
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
}

/// <summary>
/// Picks the server, in this order: --host/--key (or LRW_HOST/LRW_KEY), --profile (or LRW_PROFILE),
/// the default set with `lrw profiles use`, and finally the only saved profile when there is just one.
/// </summary>
public static class TargetResolver
{
    public const string PassphraseVariable = "LRW_PASSPHRASE";

    public static ConnectionTarget Resolve(ConnectionOptions options)
    {
        var passphrase = ReadPassphrase(options);

        var host = options.Host ?? Env("LRW_HOST");
        if (host != null)
        {
            var key = options.Key ?? Env("LRW_KEY")
                      ?? throw new UsageException("--host needs --key <path to the private key> (or set LRW_KEY).");
            return new ConnectionTarget(host.Trim(), options.User ?? Env("LRW_USER") ?? "root", Path.GetFullPath(key), passphrase, null);
        }

        var profiles = new ProfileService().LoadStrict();
        var name = options.Profile ?? Env("LRW_PROFILE") ?? CliSettings.Load().DefaultProfile;
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

        return new ConnectionTarget(profile.Host, options.User ?? profile.Username, profile.PrivateKeyPath, passphrase, profile.Name);
    }

    public static ConnectionProfile? FindProfile(IEnumerable<ConnectionProfile> profiles, string nameOrId) =>
        profiles.FirstOrDefault(p => p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? profiles.FirstOrDefault(p => p.Id.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    private static string Saved(IReadOnlyCollection<ConnectionProfile> profiles) =>
        profiles.Count == 0 ? "There are no saved profiles." : $"Saved profiles: {string.Join(", ", profiles.Select(p => p.Name))}.";

    private static string? ReadPassphrase(ConnectionOptions options)
    {
        if (options.PassphraseFile is { } file)
        {
            if (!File.Exists(file)) throw new UsageException($"Passphrase file not found: {file}");
            return File.ReadAllText(file).TrimEnd('\r', '\n');
        }

        if (options.PassphraseEnv is { } variable)
            return Env(variable) ?? throw new UsageException($"Environment variable {variable} (--passphrase-env) is not set.");

        return Env(PassphraseVariable);
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
