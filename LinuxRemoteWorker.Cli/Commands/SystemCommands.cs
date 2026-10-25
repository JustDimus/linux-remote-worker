using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.SystemInfo;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("system", "Server overview, and a raw shell for everything the other groups do not cover")]
public sealed class SystemCommands : CommandSet
{
    public SystemCommands(CommandContext context) : base(context) { }

    [Command("info", "Hostname, OS, kernel, uptime, CPU load, memory, disk, addresses, gateways, DNS and outbound IPv4/IPv6 reachability.",
        ReadOnly = true)]
    public async Task<SystemSnapshot> Info(
        [Arg("Probe outbound IPv4/IPv6 reachability (takes up to 8 seconds); --no-probe skips it")] bool probe = true) =>
        await new SystemInfoService(await SshAsync()).GetAsync(probe);

    [Command("exec", "Run a shell command on the server as the SSH user (usually root) and return its exit code, stdout and stderr. " +
                     "Prefer the dedicated commands: they validate input and roll back failed changes. " +
                     "The result is ok when the command ran; check data.exitCode (the process exit code is 1 when it is not 0).",
        Unrestricted = true, Example = "system exec \"ss -tlnp\"")]
    public async Task<Outcome> Exec(
        [Arg("The command line for the remote shell; on the CLI every remaining word is joined", Rest = true, AllowFile = true)] string command,
        [Arg("Give up after this many seconds")] int timeout = 300)
    {
        if (string.IsNullOrWhiteSpace(command)) throw OperationException.Invalid("Pass the command to run.");
        if (timeout is < 1 or > 3600) throw OperationException.Invalid("--timeout: 1 to 3600 seconds.");

        var ssh = await SshAsync();
        var r = await ssh.ExecuteAsync(command, timeout: TimeSpan.FromSeconds(timeout));
        return new Outcome($"exit code {r.ExitCode}")
        {
            Data = new { exitCode = r.ExitCode, stdout = r.Output, stderr = r.Error },
            ExitCode = r.Succeeded ? ExitCodes.Ok : ExitCodes.Failed
        };
    }
}
