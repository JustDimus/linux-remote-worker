using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Services;

/// <summary>Installed means `dotnet` is on PATH. Sdks / Runtimes are the lines of `dotnet --list-sdks` / `--list-runtimes`.</summary>
public sealed record DotnetStatus(bool Installed, string? DefaultSdk, IReadOnlyList<string> Sdks, IReadOnlyList<string> Runtimes);

/// <summary>
/// .NET SDKs installed with Microsoft's dotnet-install script into one DOTNET_ROOT, so several versions
/// coexist and are all listed by `dotnet --list-sdks` (multilevel lookup was removed in .NET 7+).
/// </summary>
public sealed class DotnetSdkService
{
    public const string Root = "/usr/lib/dotnet";

    public static readonly IReadOnlyList<string> KnownChannels = ["10.0", "9.0", "8.0"];

    // The channel ends up in an rm -rf glob, so nothing but "major.minor"
    private static readonly Regex Channel = new(@"^\d{1,2}\.\d$");

    private readonly SshService _ssh;

    public DotnetSdkService(SshService ssh)
    {
        _ssh = ssh;
    }

    public static string? ValidateChannel(string channel) =>
        Channel.IsMatch(channel) ? null : $".NET version: major.minor, for example {string.Join(", ", KnownChannels)}.";

    public async Task<DotnetStatus> GetStatusAsync()
    {
        var dotnet = await _ssh.RunCommandAsync("dotnet --version 2>/dev/null || echo ''");
        if (string.IsNullOrWhiteSpace(dotnet))
            return new DotnetStatus(false, null, [], []);

        var sdks = await _ssh.RunCommandAsync("dotnet --list-sdks 2>/dev/null");
        var runtimes = await _ssh.RunCommandAsync("dotnet --list-runtimes 2>/dev/null");
        return new DotnetStatus(true, dotnet.Trim(), Lines(sdks), Lines(runtimes));
    }

    /// <summary>Installs the SDK of a channel (it brings the .NET and ASP.NET Core runtimes along).</summary>
    public async Task InstallAsync(string channel, Action<string> onLine)
    {
        OperationException.ThrowIfInvalid(ValidateChannel(channel));

        // Use Microsoft's install script (Azure CDN has IPv6, works without IPv4).
        var script =
            $"curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh 2>&1 && " +
            $"bash /tmp/dotnet-install.sh --channel {channel} --install-dir {Root} 2>&1 && " +
            $"ln -sf {Root}/dotnet /usr/bin/dotnet && " +
            $"echo \"[ok] .NET {channel} installed into {Root}\"";

        var r = await _ssh.ExecuteStreamAsync(script, onLine);
        if (!r.Succeeded)
            throw OperationException.Failed($"Installing .NET {channel} failed (exit {r.ExitCode}) - see the output.", r.Text);
    }

    /// <summary>Removes the SDK and both runtimes of a channel from <see cref="Root"/>.</summary>
    public async Task UninstallAsync(string channel, Action<string> onLine)
    {
        OperationException.ThrowIfInvalid(ValidateChannel(channel));

        await _ssh.RunCommandStreamAsync(
            $"rm -rf {Root}/sdk/{channel}.* " +
            $"{Root}/shared/Microsoft.AspNetCore.App/{channel}.* " +
            $"{Root}/shared/Microsoft.NETCore.App/{channel}.* 2>&1; " +
            $"echo '[ok] removed {channel}'", onLine);
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
