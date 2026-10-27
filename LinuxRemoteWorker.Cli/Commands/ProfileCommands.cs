using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("profiles", "Saved servers - the same list as SAVED SERVERS in the desktop app")]
public sealed class ProfileCommands : CommandSet
{
    private readonly ProfileService _store = new();

    public ProfileCommands(CommandContext context) : base(context) { }

    [Command("list", "List saved servers, which one is the default, how each logs in (key or password), and whether its key file exists or its password is saved.",
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

    [Command("add", "Save a server: host, user, and how it logs in - the path of a private key, or a password. " +
                    "--password saves the password encrypted for this Windows user (DPAPI); --password-login saves none, so it is " +
                    "asked for or read from LRW_PASSWORD on each login. Key passphrases are never stored.",
        Local = true, Example = "profiles add prod-01 --host 203.0.113.10 --key C:\\keys\\id_ed25519")]
    public Outcome Add(
        [Arg("Profile name, for example prod-01", Positional = true)] string name,
        [Arg("Server IP address or host name")] string host,
        [Arg("Path of the OpenSSH private key on this computer (not the .pub file)")] string? key = null,
        [Arg("Log in with this password and save it encrypted (Windows only); never type it into a chat", Secret = true)]
        string? password = null,
        [Arg("Log in with a password that is not saved: LRW_PASSWORD, --password-env/--password-file, or asked for")] bool passwordLogin = false,
        [Arg("Linux user to log in as")] string user = "root",
        [Arg("Also make it the default for commands without --profile")] bool makeDefault = false)
    {
        name = name.Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(host))
            throw OperationException.Invalid("A profile needs a name and a host.");
        var usePassword = password != null || passwordLogin;
        if (usePassword == !string.IsNullOrWhiteSpace(key))
            throw OperationException.Invalid("Choose how the profile logs in: --key <path>, or --password / --password-login.");

        var profiles = _store.LoadStrict();
        if (TargetResolver.FindProfile(profiles, name) != null)
            throw OperationException.Invalid($"A profile named '{name}' already exists - use `profiles update`.");

        var profile = new ConnectionProfile
        {
            Name = name,
            Host = host.Trim(),
            Username = user.Trim()
        };
        if (usePassword) profile.UsePassword(password);
        else profile.UseKey(Path.GetFullPath(key!));
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

    [Command("update", "Change a saved server. Only the options you pass are changed. --key switches it to key login (a saved " +
                       "password is dropped); --password / --password-login switch it to password login.", Local = true, Idempotent = true)]
    public Outcome Update(
        [Arg("Profile name or id", Positional = true)] string name,
        [Arg("New profile name")] string? rename = null,
        [Arg("New host")] string? host = null,
        [Arg("New Linux user")] string? user = null,
        [Arg("Log in with this private key from now on")] string? key = null,
        [Arg("Log in with this password from now on, saved encrypted (Windows only)", Secret = true)] string? password = null,
        [Arg("Log in with a password that is not saved")] bool passwordLogin = false,
        [Arg("Drop the saved password (it is then asked for or read from LRW_PASSWORD)")] bool forgetPassword = false)
    {
        var switches = new[] { !string.IsNullOrWhiteSpace(key), password != null, passwordLogin, forgetPassword }.Count(given => given);
        if (switches > 1)
            throw OperationException.Invalid("Pass only one of --key, --password, --password-login and --forget-password.");

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
        if (!string.IsNullOrWhiteSpace(key)) profile.UseKey(Path.GetFullPath(key));
        if (password != null) profile.UsePassword(password);
        if (passwordLogin) profile.UsePassword(null);
        if (forgetPassword) profile.ProtectedPassword = null;
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

    // The encrypted password never leaves profiles.json - only whether there is one
    private static object Describe(ConnectionProfile p, CliSettings settings) => new
    {
        name = p.Name,
        host = p.Host,
        username = p.Username,
        auth = p.AuthMethod == SshAuthMethod.Password ? "password" : "key",
        privateKeyPath = p.AuthMethod == SshAuthMethod.Key ? p.PrivateKeyPath : null,
        keyFileExists = p.AuthMethod == SshAuthMethod.Key ? File.Exists(p.PrivateKeyPath) : (bool?)null,
        passwordSaved = p.AuthMethod == SshAuthMethod.Password ? p.HasSavedPassword : (bool?)null,
        isDefault = settings.DefaultProfile?.Equals(p.Name, StringComparison.OrdinalIgnoreCase) == true,
        id = p.Id
    };

    private static string? KeyWarning(ConnectionProfile p)
    {
        if (p.AuthMethod == SshAuthMethod.Password)
            return p.HasSavedPassword ? null
                : $"No password is saved: set {TargetResolver.PasswordVariable} (or --password-env / --password-file) when connecting, " +
                  "or answer the prompt in a terminal.";
        return ConnectionDiagnostics.PreflightCheck(SshCredentials.WithKey(p.Host, p.Username, p.PrivateKeyPath)) is { } problem
            ? $"{problem.Summary}. {problem.Hint}"
            : null;
    }
}
