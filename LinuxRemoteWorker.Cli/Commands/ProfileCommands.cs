using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("profiles", "Saved servers - the same list as SAVED SERVERS in the desktop app")]
public sealed class ProfileCommands : CommandSet
{
    private readonly ProfileService _store = new();

    public ProfileCommands(CommandContext context) : base(context) { }

    [Command("list", "List saved servers, which one is the default, and whether each key file exists on this computer.",
        ReadOnly = true, Local = true)]
    public object List()
    {
        var settings = CliSettings.Load();
        return new
        {
            profilesFile = ProfileService.ProfilesPath,
            defaultProfile = settings.DefaultProfile,
            profiles = _store.LoadStrict().Select(p => Describe(p, settings)).ToList()
        };
    }

    [Command("show", "Show one saved server.", ReadOnly = true, Local = true)]
    public object Show([Arg("Profile name or id", Positional = true)] string name) =>
        Describe(Find(_store.LoadStrict(), name), CliSettings.Load());

    [Command("add", "Save a server: host, user and the path of its private key. Passphrases are never stored.",
        Local = true, Example = "profiles add prod-01 --host 203.0.113.10 --key C:\\keys\\id_ed25519")]
    public Outcome Add(
        [Arg("Profile name, for example prod-01", Positional = true)] string name,
        [Arg("Server IP address or host name")] string host,
        [Arg("Path of the OpenSSH private key on this computer (not the .pub file)")] string key,
        [Arg("Linux user to log in as")] string user = "root",
        [Arg("Also make it the default for commands without --profile")] bool makeDefault = false)
    {
        name = name.Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(host))
            throw OperationException.Invalid("A profile needs a name and a host.");

        var profiles = _store.LoadStrict();
        if (TargetResolver.FindProfile(profiles, name) != null)
            throw OperationException.Invalid($"A profile named '{name}' already exists - use `profiles update`.");

        var profile = new ConnectionProfile
        {
            Name = name,
            Host = host.Trim(),
            Username = user.Trim(),
            PrivateKeyPath = Path.GetFullPath(key)
        };
        profiles.Add(profile);
        _store.Save(profiles);
        AppLog.Info($"Profile saved from the CLI: {profile.Name} ({profile.Username}@{profile.Host})");

        var settings = CliSettings.Load();
        if (makeDefault)
        {
            settings.DefaultProfile = profile.Name;
            settings.Save();
        }

        return new Outcome($"Profile {profile.Name} saved" + (makeDefault ? " and set as default" : ""))
        {
            Data = Describe(profile, settings),
            Warning = KeyWarning(profile)
        };
    }

    [Command("update", "Change a saved server. Only the options you pass are changed.", Local = true, Idempotent = true)]
    public Outcome Update(
        [Arg("Profile name or id", Positional = true)] string name,
        [Arg("New profile name")] string? rename = null,
        [Arg("New host")] string? host = null,
        [Arg("New Linux user")] string? user = null,
        [Arg("New private key path")] string? key = null)
    {
        var profiles = _store.LoadStrict();
        var profile = Find(profiles, name);
        var oldName = profile.Name;

        if (!string.IsNullOrWhiteSpace(rename))
        {
            if (TargetResolver.FindProfile(profiles.Where(p => p != profile), rename) != null)
                throw OperationException.Invalid($"A profile named '{rename}' already exists.");
            profile.Name = rename.Trim();
        }
        if (!string.IsNullOrWhiteSpace(host)) profile.Host = host.Trim();
        if (!string.IsNullOrWhiteSpace(user)) profile.Username = user.Trim();
        if (!string.IsNullOrWhiteSpace(key)) profile.PrivateKeyPath = Path.GetFullPath(key);
        _store.Save(profiles);
        AppLog.Info($"Profile updated from the CLI: {profile.Name} ({profile.Username}@{profile.Host})");

        // keep the default pointing at the renamed profile
        var settings = CliSettings.Load();
        if (settings.DefaultProfile != null && settings.DefaultProfile.Equals(oldName, StringComparison.OrdinalIgnoreCase)
            && profile.Name != oldName)
        {
            settings.DefaultProfile = profile.Name;
            settings.Save();
        }

        return new Outcome($"Profile {profile.Name} updated") { Data = Describe(profile, settings), Warning = KeyWarning(profile) };
    }

    [Command("remove", "Delete a saved server from the list. The key file itself is not touched.", Destructive = true, Local = true)]
    public Outcome Remove([Arg("Profile name or id", Positional = true)] string name)
    {
        var profiles = _store.LoadStrict();
        var profile = Find(profiles, name);
        profiles.Remove(profile);
        _store.Save(profiles);
        AppLog.Info($"Profile deleted from the CLI: {profile.Name}");

        var settings = CliSettings.Load();
        if (settings.DefaultProfile?.Equals(profile.Name, StringComparison.OrdinalIgnoreCase) == true)
        {
            settings.DefaultProfile = null;
            settings.Save();
        }
        return new Outcome($"Profile {profile.Name} removed");
    }

    [Command("use", "Make a profile the default for commands that pass neither --profile nor --host.", Local = true, Idempotent = true)]
    public Outcome Use([Arg("Profile name or id", Positional = true)] string name)
    {
        var profile = Find(_store.LoadStrict(), name);
        var settings = CliSettings.Load();
        settings.DefaultProfile = profile.Name;
        settings.Save();
        return new Outcome($"Default profile: {profile.Name} ({profile.Username}@{profile.Host})");
    }

    private static ConnectionProfile Find(IEnumerable<ConnectionProfile> profiles, string name) =>
        TargetResolver.FindProfile(profiles, name)
        ?? throw OperationException.NotFound($"No saved profile '{name}'. See `lrw profiles list`.");

    private static object Describe(ConnectionProfile p, CliSettings settings) => new
    {
        name = p.Name,
        host = p.Host,
        username = p.Username,
        privateKeyPath = p.PrivateKeyPath,
        keyFileExists = File.Exists(p.PrivateKeyPath),
        isDefault = settings.DefaultProfile?.Equals(p.Name, StringComparison.OrdinalIgnoreCase) == true,
        id = p.Id
    };

    private static string? KeyWarning(ConnectionProfile p) =>
        ConnectionDiagnostics.PreflightCheck(p.Host, p.Username, p.PrivateKeyPath) is { } problem
            ? $"{problem.Summary}. {problem.Hint}"
            : null;
}
