using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>
/// What a command returns when there is more to say than data: a message for the user, a warning, the
/// remote output worth keeping. Any other return value becomes the result's data as it is.
/// </summary>
public sealed record Outcome(string Message)
{
    public object? Data { get; init; }
    public string? Output { get; init; }
    public string? Warning { get; init; }

    /// <summary>
    /// Process exit code for a result that is reported fine but means "no": nginx -t failed, a remote
    /// command exited non-zero. The JSON still says ok:true — the operation itself worked.
    /// </summary>
    public int ExitCode { get; init; }
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Invalid = 2;
    public const int Connection = 3;
    public const int Refused = 4;
    public const int NotFound = 5;
    public const int Cancelled = 130;

    public static readonly IReadOnlyDictionary<int, string> Meaning = new Dictionary<int, string>
    {
        [Ok] = "success",
        [Failed] = "the operation failed on the server (see error.output)",
        [Invalid] = "invalid command line or input - nothing was sent to the server",
        [Connection] = "could not connect / the connection dropped (see error.hint)",
        [Refused] = "refused by a safety rule, or a destructive command without --yes",
        [NotFound] = "the thing to act on does not exist",
        [Cancelled] = "cancelled"
    };

    public static int For(FailureKind kind) => kind switch
    {
        FailureKind.Invalid => Invalid,
        FailureKind.Refused => Refused,
        FailureKind.NotFound => NotFound,
        _ => Failed
    };
}

/// <summary>Bad command line: unknown command or option, missing or malformed value.</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message, string? helpPath = null) : base(message)
    {
        HelpPath = helpPath;
    }

    /// <summary>What to put after "lrw help" to see the relevant help.</summary>
    public string? HelpPath { get; }
}

/// <summary>The server could not be reached; carries the plain-language explanation from ConnectionDiagnostics.</summary>
public sealed class ConnectionFailedException : Exception
{
    public ConnectionFailedException(ConnectionProblem problem, string target) : base(problem.Summary)
    {
        Problem = problem;
        Target = target;
    }

    public ConnectionProblem Problem { get; }
    public string Target { get; }
}
