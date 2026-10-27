using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using Renci.SshNet.Common;

namespace LinuxRemoteWorker.Cli.Session;

/// <summary>
/// The SSH connection commands run on. It connects on first use, so local commands never dial out;
/// the CLI keeps one per invocation, the MCP server one for its whole life (reconnecting when it drops).
/// </summary>
public sealed class ServerSession : IDisposable
{
    private readonly Func<ConnectionTarget> _resolveDefault;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionTarget? _target;
    private SshService? _ssh;

    // Set while no server may be used, e.g. after a failed switch: falling back to the default server
    // would run the next command on a machine the caller did not mean
    private string? _unselectedReason;

    /// <param name="resolveDefault">Picks the server when none was chosen explicitly; may throw <see cref="UsageException"/>.</param>
    public ServerSession(Func<ConnectionTarget> resolveDefault)
    {
        _resolveDefault = resolveDefault;
    }

    /// <summary>Asks for the key passphrase when the key turns out to be encrypted (interactive CLI only).</summary>
    public Func<string?>? PromptPassphrase { get; init; }

    /// <summary>Asks for the password of a password login that has none (interactive CLI only); gets "user@host".</summary>
    public Func<string, string?>? PromptPassword { get; init; }

    /// <summary>The server of the live connection, or null when not connected.</summary>
    public ConnectionTarget? Connected => _ssh?.IsConnected == true ? _target : null;

    /// <summary>The server commands will run on (resolving the default if nothing was chosen).</summary>
    public ConnectionTarget Target
    {
        get
        {
            if (_unselectedReason != null) throw new UsageException(_unselectedReason, "connection use");
            return _target ??= _resolveDefault();
        }
    }

    /// <summary>Switches to another server; the next command connects to it.</summary>
    public void Use(ConnectionTarget target)
    {
        Disconnect();
        _target = target;
        _unselectedReason = null;
        AppLog.Info($"Session switched to {target.Display}");
    }

    /// <summary>
    /// Drops the current server and refuses every command until <see cref="Use"/> picks one, instead of
    /// falling back to the default server.
    /// </summary>
    public void Unselect(string reason)
    {
        Disconnect();
        _target = null;
        _unselectedReason = reason;
        AppLog.Info($"Session has no server: {reason}");
    }

    public async Task<SshService> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_ssh is { IsConnected: true } live) return live;
            Disconnect();

            var target = Target;

            // A password login without a password: ask, when someone is there to answer
            if (target.Method == SshAuthMethod.Password && string.IsNullOrEmpty(target.Password))
            {
                if (PromptPassword?.Invoke(target.Display) is { Length: > 0 } typedPassword)
                    target = target with { Password = typedPassword };
                else
                    throw new ConnectionFailedException(new ConnectionProblem(
                        "A password is needed",
                        $"{target.Display} logs in with a password, and none was given or saved.",
                        $"Set it in the {TargetResolver.PasswordVariable} environment variable, pass --password-env <VAR> / " +
                        "--password-file <path>, or save it with the profile (desktop app: Remember password; " +
                        "`lrw profiles update <name> --password env:VAR`)."), target.Display);
            }

            if (ConnectionDiagnostics.PreflightCheck(target.Credentials) is { } problem)
                throw new ConnectionFailedException(problem, target.Display);

            var ssh = new SshService();
            var failure = await TryConnectAsync(ssh, target, ct);

            // An encrypted key without a passphrase: ask once, when someone is there to answer
            if (failure is SshPassPhraseNullOrEmptyException && target.Passphrase == null
                && PromptPassphrase?.Invoke() is { Length: > 0 } typed)
            {
                target = target with { Passphrase = typed };
                failure = await TryConnectAsync(ssh, target, ct);
            }

            if (failure != null)
            {
                ssh.Dispose();
                var explained = ConnectionDiagnostics.Explain(failure, target.Credentials);
                if (failure is SshPassPhraseNullOrEmptyException)
                    explained = explained with
                    {
                        Hint = $"Set the passphrase in the {TargetResolver.PassphraseVariable} environment variable, " +
                               "or pass --passphrase-env <VAR> / --passphrase-file <path>."
                    };
                if (failure is HostKeyChangedException)
                    explained = explained with
                    {
                        Hint = explained.Hint + " After confirming the new key: `lrw connection forget-host-key --yes`, then connect again."
                    };
                AppLog.Warn($"Connection problem: {explained.Summary} | {explained.Detail}");
                throw new ConnectionFailedException(explained, target.Display);
            }

            _target = target;
            return _ssh = ssh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Exception?> TryConnectAsync(SshService ssh, ConnectionTarget target, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => ssh.Connect(target.Credentials), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex;
        }
    }

    private void Disconnect()
    {
        _ssh?.Dispose();
        _ssh = null;
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}
