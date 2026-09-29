namespace LinuxRemoteWorker.Core;

/// <summary>Outcome of a remote command: its exit code plus raw (untrimmed) stdout and stderr.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>stdout followed by stderr, trimmed — what to show the user.</summary>
    public string Text => string.Join("\n", new[] { Output.Trim(), Error.Trim() }.Where(s => s.Length > 0));

    /// <summary>Non-empty, trimmed stdout lines.</summary>
    public List<string> Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
