using System.Diagnostics;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Update;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("update", "Update lrw itself to the newest release, together with its Claude Code skill")]
public sealed class UpdateCommands : CommandSet
{
    public UpdateCommands(CommandContext context) : base(context) { }

    [Command("", "Update lrw to the newest release. Runs that release's own install script (install-cli.ps1 on Windows, " +
                 "install-cli.sh on macOS and Linux) once it matches the release's SHA256SUMS.txt; the script replaces the " +
                 "program and refreshes the Claude Code skill. --check only compares versions. Updates the copy the install " +
                 "scripts put in place; any other copy (a development build) is refused.",
        Local = true, Surface = Surface.Cli, Example = "update --check")]
    public async Task<object> Update(
        [Arg("Only report whether a newer release exists; nothing is downloaded")] bool check = false,
        [Arg("Install this release (e.g. v0.2.1) instead of the newest, also an older one")] string? version = null,
        [Arg("Run the install script even when this version is already installed")] bool force = false,
        [Arg("Register the MCP server again, connecting to this saved profile by default")] string? mcpProfile = null,
        [Arg("Register the MCP server in Claude Code when it is not yet (--no-mcp to leave Claude Code alone)")] bool mcp = true,
        [Arg("Add or refresh the Claude Code skill (--no-skill to leave it alone)")] bool skill = true,
        [Arg("Put lrw on PATH when it is not there (--no-path to leave PATH alone)")] bool path = true,
        CancellationToken ct = default)
    {
        if (check && version != null) throw OperationException.Invalid("--check compares with the newest release; leave out --version.");

        var current = Version.Parse(HelpWriter.Version);
        using var http = ReleaseChannel.CreateClient(TimeSpan.FromMinutes(2));
        var channel = new ReleaseChannel(http);
        var target = version == null
            ? await LatestAsync(channel, ct)
            : ReleaseChannel.ParseVersion(version) ?? throw OperationException.Invalid($"'{version}' is not a release version such as v0.2.1.");

        if (check)
        {
            var available = target > current;
            return new Outcome(available
                ? $"lrw {ReleaseChannel.Tag(target)} is available (you have {current.ToString(3)}) - run: lrw update"
                : $"lrw {current.ToString(3)} is up to date.")
            {
                Data = new { current = current.ToString(3), latest = target.ToString(3), updateAvailable = available }
            };
        }

        var exe = InstallLocation.CurrentPath;
        if (!InstallLocation.IsStandard(exe, InstallLocation.StandardPath))
            throw OperationException.Refused(
                $"This lrw runs from {exe ?? "an unknown place"}, not from {InstallLocation.StandardPath} where the install scripts " +
                "put it. Update this copy the way it was made (rebuild a development build), or install a release with the install script.");

        if (!force && (version == null ? target <= current : target == current))
            return new Outcome($"lrw {current.ToString(3)} is up to date.")
            {
                Data = new { previous = current.ToString(3), version = current.ToString(3), updated = false }
            };

        var script = OperatingSystem.IsWindows() ? "install-cli.ps1" : "install-cli.sh";
        var folder = Directory.CreateTempSubdirectory("lrw-update-");
        try
        {
            Lines($"Checking {script} of {ReleaseChannel.Tag(target)} against its SHA256SUMS.txt...");
            var downloaded = await DownloadAsync(channel, target, script, folder.FullName, ct);
            var arguments = ScriptArguments(OperatingSystem.IsWindows(), target, force, mcpProfile, mcp, skill, path);
            var exitCode = await RunScriptAsync(downloaded, arguments, ct);
            if (exitCode != 0) throw OperationException.Failed($"The install script stopped with exit code {exitCode} - see its output above.");
        }
        finally
        {
            try { folder.Delete(recursive: true); } catch (IOException) { }
        }

        return new Outcome($"lrw is now {ReleaseChannel.Tag(target)} (was {current.ToString(3)}).")
        {
            Data = new { previous = current.ToString(3), version = target.ToString(3), updated = true }
        };
    }

    /// <summary>The install script's options for these choices; MCP and skill flags pass through as the script names them.</summary>
    internal static IReadOnlyList<string> ScriptArguments(bool windows, Version target, bool force, string? mcpProfile, bool mcp, bool skill, bool path)
    {
        var arguments = new List<string> { windows ? "-Version" : "--version", ReleaseChannel.Tag(target) };
        if (force) arguments.Add(windows ? "-Force" : "--force");
        if (mcpProfile != null) arguments.AddRange([windows ? "-McpProfile" : "--mcp-profile", mcpProfile]);
        if (!mcp) arguments.Add(windows ? "-NoMcp" : "--no-mcp");
        if (!skill) arguments.Add(windows ? "-NoSkill" : "--no-skill");
        if (!path) arguments.Add(windows ? "-NoPath" : "--no-path");
        return arguments;
    }

    private static async Task<Version> LatestAsync(ReleaseChannel channel, CancellationToken ct)
    {
        try
        {
            return await channel.LatestVersionAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw OperationException.Failed($"Could not ask GitHub for the newest release: {ex.Message}");
        }
    }

    private static async Task<string> DownloadAsync(ReleaseChannel channel, Version target, string file, string folder, CancellationToken ct)
    {
        try
        {
            return await channel.DownloadAsync(target, file, folder, ct);
        }
        catch (HttpRequestException ex)
        {
            throw OperationException.Failed($"Could not download {file} of {ReleaseChannel.Tag(target)} (releases before the update command have none): {ex.Message}");
        }
    }

    // Its output streams as live output; on Windows the script renames the running lrw.exe out of the way
    private async Task<int> RunScriptAsync(string script, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            : new ProcessStartInfo("bash");
        if (OperatingSystem.IsWindows())
        {
            foreach (var option in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(option);
            // Started from PowerShell 7, lrw inherits its PSModulePath; Windows PowerShell would then find PowerShell 7's
            // modules first, fail to load them and miss their commands. Without the variable it uses its own folders.
            start.Environment.Remove("PSModulePath");
        }
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var process = Process.Start(start) ?? throw OperationException.Failed($"Could not start {start.FileName}.");
        var output = PumpAsync(process.StandardOutput);
        var errors = PumpAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(output, errors);
        return process.ExitCode;

        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
                if (line.Length > 0) Lines(line);
        }
    }
}
