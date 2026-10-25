using System.Diagnostics;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("connection", "Which server commands run on, and whether it can be reached")]
public sealed class ConnectionCommands : CommandSet
{
    public ConnectionCommands(CommandContext context) : base(context) { }

    [Command("test", "Connect and report the server, user, OS and round-trip time. A failure says why in plain words " +
                     "(DNS, port 22 blocked, key rejected, passphrase needed) and what to do about it.", ReadOnly = true)]
    public async Task<object> Test()
    {
        var watch = Stopwatch.StartNew();
        var ssh = await SshAsync();
        var connectMs = watch.ElapsedMilliseconds;

        watch.Restart();
        var r = await ssh.ExecuteAsync(
            "printf '%s|%s|%s|%s' \"$(whoami)\" \"$(hostname)\" \"$(uname -sr)\" \"$(. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\")\"");
        var roundTripMs = watch.ElapsedMilliseconds;
        var parts = r.Output.Trim().Split('|');
        var target = Context.Session.Target;

        return new
        {
            server = target.Display,
            profile = target.ProfileName,
            host = target.Host,
            user = target.Username,
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
            target = target == null ? null : new { host = target.Host, user = target.Username, key = target.KeyPath, profile = target.ProfileName },
            problem,
            defaultProfile = CliSettings.Load().DefaultProfile,
            profilesFile = ProfileService.ProfilesPath,
            cliSettingsFile = CliSettings.SettingsPath,
            logDirectory = AppLog.LogDirectory,
            version = HelpWriter.Version
        };
    }

    [Command("use", "Switch this session to another server - a saved profile, or host + key - and test the connection.",
        Surface = Surface.Mcp)]
    public async Task<object> Use(
        [Arg("Saved profile name (see profiles_list)")] string? profile = null,
        [Arg("Server address, when not using a profile")] string? host = null,
        [Arg("Linux user, with host")] string? user = null,
        [Arg("Private key path on this computer, with host")] string? key = null,
        [Arg("Name of an environment variable of the MCP server process that holds the key passphrase")] string? passphraseEnv = null)
    {
        if (profile == null && host == null)
            throw OperationException.Invalid("Pass a profile name, or host and key.");

        Context.Session.Use(TargetResolver.Resolve(new ConnectionOptions
        {
            Profile = profile,
            Host = host,
            User = user,
            Key = key,
            PassphraseEnv = passphraseEnv
        }));
        return await Test();
    }
}
