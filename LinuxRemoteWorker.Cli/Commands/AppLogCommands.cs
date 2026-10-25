using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

public enum LogSource { App, Cli, Mcp, All }

[CommandGroup("applog", "This tool's own diagnostic log on this computer (desktop app, CLI and MCP server) - no server needed")]
public sealed class AppLogCommands : CommandSet
{
    private const int MaxLines = 20000;

    public AppLogCommands(CommandContext context) : base(context) { }

    [Command("list", "Log files in %AppData%\\LinuxRemoteWorker\\logs (app-, cli- and mcp-<date>.log), newest first.",
        ReadOnly = true, Local = true)]
    public object List() => new
    {
        directory = AppLog.LogDirectory,
        files = AppLog.ListFiles().Select(name =>
        {
            var info = new FileInfo(AppLog.PathOf(name));
            return new { name, source = name.Split('-')[0], size = info.Length, modified = info.LastWriteTime };
        }).ToList()
    };

    [Command("read", "The last lines of a log file - by default the newest file of the desktop app. --filter keeps matching lines only (e.g. ERROR, an IP, a command).",
        ReadOnly = true, Local = true, Example = "applog read --filter ERROR --lines 50")]
    public object Read(
        [Arg("File name from applog list (overrides --source)")] string? file = null,
        [Arg("Whose log: app (desktop), cli, mcp, or all (the newest of any)")] LogSource source = LogSource.App,
        [Arg("How many lines (counted after filtering)")] int lines = 200,
        [Arg("Only lines containing this text (case-insensitive)")] string? filter = null)
    {
        var name = file ?? AppLog.ListFiles().FirstOrDefault(f =>
                       source == LogSource.All || f.StartsWith(source.ToString().ToLowerInvariant() + "-", StringComparison.Ordinal))
                   ?? throw OperationException.NotFound($"No {source.ToString().ToLowerInvariant()} log files in {AppLog.LogDirectory}.");
        if (name.IndexOfAny(['/', '\\']) >= 0 || !File.Exists(AppLog.PathOf(name)))
            throw OperationException.NotFound($"No log file {name} in {AppLog.LogDirectory}.");

        var count = Math.Clamp(lines, 1, MaxLines);
        var text = AppLog.ReadTail(name, string.IsNullOrWhiteSpace(filter) ? count : MaxLines);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var matching = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .TakeLast(count)
                .ToList();
            text = matching.Count == 0 ? $"(no lines matching \"{filter}\")" : string.Join('\n', matching).TrimEnd();
        }

        return new { file = AppLog.PathOf(name), filter, text };
    }

    [Command("path", "Where the log files, saved profiles and CLI settings live on this computer.", ReadOnly = true, Local = true)]
    public object PathInfo() => new
    {
        logDirectory = AppLog.LogDirectory,
        currentFile = AppLog.CurrentFile,
        profilesFile = ProfileService.ProfilesPath,
        cliSettingsFile = CliSettings.SettingsPath
    };
}
