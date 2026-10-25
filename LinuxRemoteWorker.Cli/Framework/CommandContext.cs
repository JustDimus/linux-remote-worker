using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>What a command can reach while it runs: the server session and a sink for live output.</summary>
public sealed class CommandContext
{
    private const int KeptLines = 40;

    private readonly Action<string> _onLine;
    private readonly Queue<string> _recent = new();

    /// <param name="onLine">Where live output goes (stderr for the CLI, progress notifications for MCP).</param>
    /// <param name="progressVisible">Whether the reader sees the live output; when not, its tail is attached to the result.</param>
    public CommandContext(ServerSession session, Action<string> onLine, bool progressVisible)
    {
        Session = session;
        _onLine = onLine;
        ProgressVisible = progressVisible;
    }

    public ServerSession Session { get; }

    public bool ProgressVisible { get; }

    public int ProgressLineCount { get; private set; }

    /// <summary>The last lines of live output, for readers that could not watch it.</summary>
    public IReadOnlyCollection<string> RecentLines => _recent;

    public void Progress(string line)
    {
        lock (_recent)
        {
            ProgressLineCount++;
            _recent.Enqueue(line);
            if (_recent.Count > KeptLines) _recent.Dequeue();
        }
        _onLine(line);
    }

    public Task<SshService> SshAsync(CancellationToken ct = default) => Session.ConnectAsync(ct);
}

/// <summary>Base of every [CommandGroup] class; one instance is created per command invocation.</summary>
public abstract class CommandSet
{
    protected CommandSet(CommandContext context)
    {
        Context = context;
    }

    protected CommandContext Context { get; }

    protected Task<SshService> SshAsync() => Context.SshAsync();

    /// <summary>Live output sink, to hand to services that stream (installs, builds, certbot).</summary>
    protected Action<string> Lines => Context.Progress;
}
