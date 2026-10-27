using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Update;

/// <summary>
/// At most once a day, an interactive run of the installed lrw asks GitHub in the background whether a newer release
/// exists and mentions it in one line on stderr. The check runs while the command does; at the end it gets a short
/// moment more, and an unfinished check is dropped. LRW_NO_UPDATE_CHECK=1 turns it off.
/// </summary>
public sealed class UpdateNotifier
{
    public const string DisableVariable = "LRW_NO_UPDATE_CHECK";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitAtEnd = TimeSpan.FromSeconds(1.5);

    private readonly Task<Version> _check;

    private UpdateNotifier(Task<Version> check)
    {
        _check = check;
    }

    /// <summary>Starts a check when one is due for the installed lrw; null otherwise.</summary>
    public static UpdateNotifier? StartIfDue()
    {
        if (Environment.GetEnvironmentVariable(DisableVariable) is { Length: > 0 }) return null;
        if (!InstallLocation.IsStandard(InstallLocation.CurrentPath, InstallLocation.StandardPath)) return null;
        if (!IsDue(CliSettings.Load().UpdateCheckedUtc, DateTime.UtcNow)) return null;

        return new UpdateNotifier(Task.Run(async () =>
        {
            using var http = ReleaseChannel.CreateClient(CheckTimeout);
            return await new ReleaseChannel(http).LatestVersionAsync(CancellationToken.None);
        }));
    }

    /// <summary>Records the check and writes the notice when a newer release exists.</summary>
    public async Task FinishAsync(TextWriter stderr)
    {
        await Task.WhenAny(_check, Task.Delay(WaitAtEnd));
        // A failed or unfinished check counts too: offline or slow, the next try is a day later
        Record(DateTime.UtcNow);
        if (_check.IsFaulted) AppLog.Info($"Update check failed: {_check.Exception!.GetBaseException().Message}");
        if (_check.IsCompletedSuccessfully && Notice(Version.Parse(HelpWriter.Version), _check.Result) is { } line)
            stderr.WriteLine(line);
    }

    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc;

    public static string? Notice(Version current, Version latest) =>
        latest > current ? $"A new version of lrw is available: {ReleaseChannel.Tag(latest)} (you have {current.ToString(3)}). Run: lrw update" : null;

    // A fresh copy of the settings: the command that just ran may have changed them
    private static void Record(DateTime nowUtc)
    {
        try
        {
            var settings = CliSettings.Load();
            settings.UpdateCheckedUtc = nowUtc;
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not record the update check in {CliSettings.SettingsPath}: {ex.Message}");
        }
    }
}
