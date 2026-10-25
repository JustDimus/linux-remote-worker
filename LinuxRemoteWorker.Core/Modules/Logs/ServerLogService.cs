using System.Globalization;
using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Modules.Logs;

/// <summary>Where a service should write its file logs: the directory and the suggested file to point Serilog/NLog at.</summary>
public sealed record AppLogLocation(string App, string Directory, string SuggestedFile);

/// <summary>
/// Server-side logs: the systemd journal of a unit, and the per-app file logs under /srv/lrw/logs/&lt;app&gt;.
/// </summary>
public sealed class ServerLogService
{
    public const int MaxLines = 10000;

    // "1 hour ago", "today", "2026-09-01 12:00:00", "-15m" — quoted anyway, but nothing beyond journalctl's syntax
    private static readonly Regex Since = new(@"^[A-Za-z0-9 :.+-]{1,40}$");
    private static readonly Regex Unit = new(@"^[A-Za-z0-9][A-Za-z0-9_.@:-]{0,127}$");

    private readonly SshService _ssh;
    private readonly BootstrapService _bootstrap;

    public ServerLogService(SshService ssh)
    {
        _ssh = ssh;
        _bootstrap = new BootstrapService(ssh);
    }

    public static string? ValidateSince(string since) =>
        Since.IsMatch(since) ? null : "Since: a journalctl time such as '1 hour ago', 'today' or '2026-09-01 12:00'.";

    public static string? ValidateUnit(string unit) =>
        Unit.IsMatch(unit) && !unit.Contains("..") ? null : $"'{unit}' is not a valid systemd unit name.";

    /// <summary>A file directly inside an app's log directory - no paths.</summary>
    public static string? ValidateFileName(string file) =>
        file.Length > 0 && file is not ("." or "..") && file.IndexOfAny(['/', '\\']) < 0 && !file.Any(char.IsControl)
            ? null
            : $"'{file}' is not a file name inside the log directory.";

    /// <summary>Apps that have an lrw-*.service unit.</summary>
    public async Task<List<string>> ListAppsAsync()
    {
        await _bootstrap.EnsureAsync();
        return (await new AppServiceManager(_ssh).ListUnitNamesAsync()).Select(AppServiceManager.AppNameOf).ToList();
    }

    // ── journalctl (systemd captured stdout/stderr) ─────────────────────────

    /// <summary>The last <paramref name="lines"/> journal lines of a unit since a point in time; empty when there are none.</summary>
    public async Task<string> JournalAsync(string unit, string since, int lines)
    {
        OperationException.ThrowIfInvalid(ValidateUnit(unit) ?? ValidateSince(since));
        return await _ssh.RunCommandAsync(
            $"journalctl -u {unit} --since {Shell.Quote(since)} --no-pager 2>&1 | tail -{Clamp(lines)}");
    }

    /// <summary>Streams new journal lines (after the last <paramref name="lines"/>) until cancelled.</summary>
    public async Task FollowJournalAsync(string unit, int lines, Action<string> onLine, CancellationToken ct)
    {
        OperationException.ThrowIfInvalid(ValidateUnit(unit));
        await _ssh.RunCommandStreamAsync($"journalctl -u {unit} -f -n {Clamp(lines)}", onLine, ct);
    }

    // ── File logs ──────────────────────────────────────────────────────────

    /// <summary>Makes sure the app's log directory exists and is writable by the service user.</summary>
    public async Task<AppLogLocation> EnsureLogDirAsync(string app)
    {
        OperationException.ThrowIfInvalid(AppServiceManager.ValidateAppName(app));
        var dir = DeployPaths.LogDir(app);
        await _ssh.RunCommandAsync(
            $"mkdir -p {Shell.Quote(dir)} && chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(dir)}");
        return new AppLogLocation(app, dir, $"{dir}/{app}.log");
    }

    /// <summary>Files in the app's log directory, most recently written first.</summary>
    public async Task<List<RemoteFileInfo>> ListLogFilesAsync(string app)
    {
        OperationException.ThrowIfInvalid(AppServiceManager.ValidateAppName(app));
        var dir = DeployPaths.LogDir(app);
        var output = (await _ssh.ExecuteAsync(
            // -L: a symlinked log (current.log -> app-20260901.log) is listed with its target's size and time
            $"find -L {Shell.Quote(dir)} -maxdepth 1 -type f -printf '%T@ %s %f\\n' 2>/dev/null | sort -rn")).Output;

        var files = new List<RemoteFileInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "1756729953.1234567890 52344 shop-api20260901.log" — the name may contain spaces
            var parts = line.Split(' ', 3);
            if (parts.Length < 3
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch)
                || !long.TryParse(parts[1], out var size)) continue;
            files.Add(new RemoteFileInfo(parts[2], $"{dir}/{parts[2]}", false, size,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)).UtcDateTime));
        }
        return files;
    }

    /// <summary>The last <paramref name="lines"/> lines of a file log.</summary>
    public async Task<string> TailLogFileAsync(string app, string file, int lines)
    {
        var path = LogFilePath(app, file);
        var r = await _ssh.ExecuteAsync($"tail -n {Clamp(lines)} {Shell.Quote(path)} 2>&1");
        if (!r.Succeeded) throw OperationException.NotFound(r.Text);
        return r.Output.TrimEnd();
    }

    public async Task DownloadLogFileAsync(string app, string file, string localPath) =>
        await _ssh.DownloadFileAsync(LogFilePath(app, file), localPath);

    private static string LogFilePath(string app, string file)
    {
        OperationException.ThrowIfInvalid(AppServiceManager.ValidateAppName(app) ?? ValidateFileName(file));
        return $"{DeployPaths.LogDir(app)}/{file}";
    }

    private static int Clamp(int lines) => Math.Clamp(lines, 1, MaxLines);
}
