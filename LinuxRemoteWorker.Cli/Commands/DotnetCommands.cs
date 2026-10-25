using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("dotnet", ".NET SDKs on the server (all versions side by side in /usr/lib/dotnet)")]
public sealed class DotnetCommands : CommandSet
{
    public DotnetCommands(CommandContext context) : base(context) { }

    private async Task<DotnetSdkService> DotnetAsync() => new(await SshAsync());

    [Command("status", "Whether dotnet is installed, the default SDK, and every installed SDK and runtime.", ReadOnly = true)]
    public async Task<DotnetStatus> Status() => await (await DotnetAsync()).GetStatusAsync();

    [Command("install", "Install a .NET SDK channel with Microsoft's dotnet-install script (brings the .NET and ASP.NET Core runtimes).",
        Streams = true, Idempotent = true, Example = "dotnet install --version 8.0")]
    public async Task<Outcome> Install([Arg("Channel, major.minor: 10.0, 9.0, 8.0 …")] string version = "10.0")
    {
        var dotnet = await DotnetAsync();
        await dotnet.InstallAsync(version, Lines);
        return new Outcome($".NET {version} installed") { Data = await dotnet.GetStatusAsync() };
    }

    [Command("uninstall", "Remove the SDK and runtimes of a channel. Services built for it stop working.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> Uninstall([Arg("Channel, major.minor, e.g. 9.0")] string version)
    {
        var dotnet = await DotnetAsync();
        await dotnet.UninstallAsync(version, Lines);
        return new Outcome($".NET {version} removed") { Data = await dotnet.GetStatusAsync() };
    }
}
