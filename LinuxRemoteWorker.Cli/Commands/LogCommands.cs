using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Logs;
using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("logs", "Server logs: the systemd journal of any unit, and the file logs of deployed apps")]
public sealed class LogCommands : CommandSet
{
    private const int MaxFollowSeconds = 600;

    public LogCommands(CommandContext context) : base(context) { }

    private async Task<ServerLogService> LogsAsync() => new(await SshAsync());

    [Command("apps", "Apps that have an lrw-*.service unit (the names the other logs commands take).", ReadOnly = true)]
    public async Task<List<string>> Apps() => await (await LogsAsync()).ListAppsAsync();

    [Command("journal", "journalctl of a deployed app (or of any unit with --unit, e.g. nginx, postgresql, ssh). " +
                        "--follow streams new lines for --seconds (on a terminal: until Ctrl+C) and returns them.",
        ReadOnly = true, Example = "logs journal shop-api --since \"30 min ago\" --lines 200")]
    public async Task<object> Journal(
        [Arg("App name (unit lrw-<app>.service)", Positional = true)] string? app = null,
        [Arg("Any systemd unit instead of an app, e.g. nginx or postgresql")] string? unit = null,
        [Arg("journalctl --since: '1 hour ago', 'today', '2026-09-01 12:00' …")] string since = "1 hour ago",
        [Arg("At most this many of the newest lines")] int lines = 500,
        [Arg("Keep streaming new lines")] bool follow = false,
        [Arg("With --follow: stop after this many seconds (0 = until Ctrl+C, CLI only)")] int seconds = 30,
        CancellationToken ct = default)
    {
        var unitName = UnitOf(app, unit);
        var logs = await LogsAsync();

        if (!follow)
        {
            var text = await logs.JournalAsync(unitName, since, lines);
            return new { unit = unitName, since, text = string.IsNullOrWhiteSpace(text) ? "(no journal entries)" : text };
        }

        if (seconds is < 0 or > MaxFollowSeconds) throw OperationException.Invalid($"--seconds: 0 to {MaxFollowSeconds}.");
        if (seconds == 0 && !Context.ProgressVisible)
            throw OperationException.Invalid("Following without a time limit only works on a terminal - pass seconds.");

        var collected = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); // Ctrl+C ends it too
        if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));

        var streaming = logs.FollowJournalAsync(unitName, Math.Min(lines, 50), line =>
        {
            lock (collected) collected.Add(line);
            Lines(line);
        }, stop.Token);

        // journalctl -f only notices cancellation when a new line arrives; stop waiting at the deadline regardless
        await Task.WhenAny(streaming, Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { }));
        if (streaming.IsFaulted) await streaming; // surfaces why journalctl could not run
        lock (collected)
            return new { unit = unitName, followedSeconds = seconds, lines = collected.ToList() };
    }

    [Command("files", "File logs of an app in /srv/lrw/logs/<app>, newest first.", ReadOnly = true)]
    public async Task<object> Files([Arg("App name", Positional = true)] string app)
    {
        var logs = await LogsAsync();
        return new { directory = DeployPaths.LogDir(app), files = await logs.ListLogFilesAsync(app) };
    }

    [Command("read", "The last lines of one of an app's file logs.", ReadOnly = true)]
    public async Task<object> Read(
        [Arg("App name", Positional = true)] string app,
        [Arg("File name in the app's log directory (see logs files)", Positional = true)] string file,
        [Arg("How many lines from the end")] int lines = 1000)
    {
        var text = await (await LogsAsync()).TailLogFileAsync(app, file, lines);
        return new { path = $"{DeployPaths.LogDir(app)}/{file}", text = text.Length == 0 ? "(file is empty)" : text };
    }

    [Command("download", "Download one of an app's file logs to this computer.", ReadOnly = true)]
    public async Task<Outcome> Download(
        [Arg("App name", Positional = true)] string app,
        [Arg("File name in the app's log directory", Positional = true)] string file,
        [Arg("Local file or existing folder; default: the current folder")] string? to = null)
    {
        OperationException.ThrowIfInvalid(ServerLogService.ValidateFileName(file));
        var local = FileCommands.LocalTarget(to, file);
        await (await LogsAsync()).DownloadLogFileAsync(app, file, local);
        var bytes = new FileInfo(local).Length;
        return new Outcome($"Downloaded to {local} ({bytes} bytes)") { Data = new { remote = $"{DeployPaths.LogDir(app)}/{file}", local, bytes } };
    }

    [Command("path", "Where an app should write its file logs (point Serilog/NLog at suggestedFile). Creates the directory, writable by the service user.",
        Idempotent = true)]
    public async Task<AppLogLocation> PathOf([Arg("App name", Positional = true)] string app) =>
        await (await LogsAsync()).EnsureLogDirAsync(app);

    private static string UnitOf(string? app, string? unit)
    {
        if (!string.IsNullOrWhiteSpace(unit))
        {
            OperationException.ThrowIfInvalid(ServerLogService.ValidateUnit(unit));
            return unit;
        }
        if (string.IsNullOrWhiteSpace(app))
            throw OperationException.Invalid("Pass an app name (see `lrw logs apps`) or --unit <systemd unit>.");

        OperationException.ThrowIfInvalid(AppServiceManager.ValidateAppName(app));
        return DeployPaths.UnitName(app);
    }
}
