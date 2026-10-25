namespace LinuxRemoteWorker.Core;

/// <summary>Why an operation did not happen. The desktop app only shows the message; the CLI maps it to an exit code.</summary>
public enum FailureKind
{
    /// <summary>The input was rejected before anything reached the server.</summary>
    Invalid,

    /// <summary>A safety rule refused the operation (it would lock you out, leave nginx broken, …).</summary>
    Refused,

    /// <summary>The thing to act on does not exist.</summary>
    NotFound,

    /// <summary>The server ran the operation and it failed.</summary>
    Failed
}

/// <summary>
/// An operation that could not be carried out, with a message written for the user. Derives from
/// <see cref="InvalidOperationException"/> so existing catch blocks treat it exactly as before.
/// </summary>
public sealed class OperationException : InvalidOperationException
{
    public FailureKind Kind { get; }

    /// <summary>Output of the remote command that failed, when it says more than the message.</summary>
    public string? Output { get; }

    public OperationException(FailureKind kind, string message, string? output = null) : base(message)
    {
        Kind = kind;
        Output = string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    public static OperationException Invalid(string message) => new(FailureKind.Invalid, message);

    public static OperationException Refused(string message) => new(FailureKind.Refused, message);

    public static OperationException NotFound(string message) => new(FailureKind.NotFound, message);

    public static OperationException Failed(string message, string? output = null) => new(FailureKind.Failed, message, output);

    /// <summary>Throws <see cref="Invalid"/> when a validator returned an error message.</summary>
    public static void ThrowIfInvalid(string? error)
    {
        if (error != null) throw Invalid(error);
    }
}
