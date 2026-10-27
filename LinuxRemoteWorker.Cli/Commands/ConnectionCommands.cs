using System.Diagnostics;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("connection", "Which server commands run on, and whether it can be reached")]
public sealed class ConnectionCommands : CommandSet
{
    public ConnectionCommands(CommandContext context) : base(context) { }

    [Command("test", "Connect and report the server, user, how it logged in, the server's host key, OS and round-trip time. " +
                     "A failure says why in plain words (DNS, port 22 blocked, key or password rejected, passphrase needed, " +
                     "server key changed) and what to do about it.", ReadOnly = true)]
    public async Task<object> Test()
    {
        var watch = Stopwatch.StartNew();
        var ssh = await SshAsync();
        var connectMs = watch.ElapsedMilliseconds;

        watch.Restart();
        var r = await ssh.ExecuteAsync(
            "printf '%s|%s|%s|%s' \"$(whoami)\" \"$(hostname)\" \"$(uname -sr)\" \"$(. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\")\"",
            asLoginUser: true);
        var roundTripMs = watch.ElapsedMilliseconds;
        var parts = r.Output.Trim().Split('|');
        var target = Context.Session.Target;

        return new
        {
            server = target.Display,
            profile = target.ProfileName,
            host = target.Host,
            user = target.Username,
            auth = target.Method == SshAuthMethod.Password ? "password" : "key",
            privileges = ssh.Privileges,
            hostKey = ssh.ServerKey?.ToString(),
            hostKeyNew = ssh.ServerKeyIsNew,
            remoteUser = parts.ElementAtOrDefault(0),
            hostname = parts.ElementAtOrDefault(1),
            kernel = parts.ElementAtOrDefault(2),
            os = parts.ElementAtOrDefault(3),
            connectMs,
            roundTripMs
        };
    }

    [Command("show", "Show which server commands would run on and where local files live, without connecting.",
        ReadOnly = true, Local = true)]
    public object Show()
    {
        ConnectionTarget? target = null;
        string? problem = null;
        try
        {
            target = Context.Session.Target;
        }
        catch (UsageException ex)
        {
            problem = ex.Message;
        }

        return new
        {
            target = target == null ? null : new
            {
                host = target.Host,
                user = target.Username,
                auth = target.Method == SshAuthMethod.Password ? "password" : "key",
                key = target.Method == SshAuthMethod.Key ? target.KeyPath : null,
                passwordAvailable = target.Method == SshAuthMethod.Password ? !string.IsNullOrEmpty(target.Password) : (bool?)null,
                profile = target.ProfileName,
                knownHostKey = HostKeyStore.Default.Lookup(target.Host)?.Key.ToString()
            },
            problem,
            defaultProfile = CliSettings.Load().DefaultProfile,
            profilesFile = ProfileService.ProfilesPath,
            cliSettingsFile = CliSettings.SettingsPath,
            knownHostsFile = HostKeyStore.Default.FilePath,
            logDirectory = AppLog.LogDirectory,
            version = HelpWriter.Version
        };
    }

    [Command("use", "Switch this session to another server - a saved profile, or host + key (or host + password variable) - and test the connection.",
        Surface = Surface.Mcp)]
    public async Task<object> Use(
        [Arg("Saved profile name (see profiles_list)")] string? profile = null,
        [Arg("Server address, when not using a profile")] string? host = null,
        [Arg("Linux user, with host")] string? user = null,
        [Arg("Private key path on this computer, with host")] string? key = null,
        [Arg("Name of an environment variable of the MCP server process that holds the key passphrase")] string? passphraseEnv = null,
        [Arg("Name of an environment variable of the MCP server process that holds the login password (never the password itself)")]
        string? passwordEnv = null)
    {
        // The caller asked for a different server: until the switch succeeds, no server is selected at all,
        // so a failed switch can never leave the next command running on the previous or default server
        Context.Session.Unselect("The last connection_use failed, so no server is selected. " +
                                 "Call connection_use again with a valid profile (see profiles_list) or host + key.");

        if (string.IsNullOrWhiteSpace(profile) && string.IsNullOrWhiteSpace(host))
            throw OperationException.Invalid("Pass a profile name, or host and key.");

        Context.Session.Use(TargetResolver.Resolve(new ConnectionOptions
        {
            Profile = NullIfBlank(profile),
            Host = NullIfBlank(host),
            User = NullIfBlank(user),
            Key = NullIfBlank(key),
            PassphraseEnv = NullIfBlank(passphraseEnv),
            PasswordEnv = NullIfBlank(passwordEnv)
        }));
        return await Test();
    }

    [Command("forget-host-key", "Forget the SSH host key remembered for the server, so the next connection remembers the key it presents. " +
                                "Only after the user confirmed the new key is genuine (server reinstalled, keys regenerated): a changed key " +
                                "is also what someone intercepting the connection would show.",
        Destructive = true, Local = true)]
    public Outcome ForgetHostKey([Arg("Server address (default: the selected server)")] string? host = null)
    {
        var target = string.IsNullOrWhiteSpace(host) ? Context.Session.Target.Host : host.Trim();
        var known = HostKeyStore.Default.Lookup(target);
        return HostKeyStore.Default.Forget(target)
            ? new Outcome($"Forgot the host key of {target} ({known!.Key}); the next connection remembers the key it presents.")
            : new Outcome($"No host key was remembered for {target}.");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
