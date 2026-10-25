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

    /// <param name="resolveDefault">Picks the server when none was chosen explicitly; may throw <see cref="UsageException"/>.</param>
    public ServerSession(Func<ConnectionTarget> resolveDefault)
    {
        _resolveDefault = resolveDefault;
    }

    /// <summary>Asks for the key passphrase when the key turns out to be encrypted (interactive CLI only).</summary>
    public Func<string?>? PromptPassphrase { get; init; }

    /// <summary>The server of the live connection, or null when not connected.</summary>
    public ConnectionTarget? Connected => _ssh?.IsConnected == true ? _target : null;

    /// <summary>The server commands will run on (resolving the default if nothing was chosen).</summary>
    public ConnectionTarget Target => _target ??= _resolveDefault();

    /// <summary>Switches to another server; the next command connects to it.</summary>
    public void Use(ConnectionTarget target)
    {
        Disconnect();
        _target = target;
        AppLog.Info($"Session switched to {target.Display}");
    }

    public async Task<SshService> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_ssh is { IsConnected: true } live) return live;
            Disconnect();

            var target = Target;
            if (ConnectionDiagnostics.PreflightCheck(target.Host, target.Username, target.KeyPath) is { } problem)
                throw new ConnectionFailedException(problem, target.Display);

            var ssh = new SshService();
            var passphrase = target.Passphrase;
            var failure = await TryConnectAsync(ssh, target, passphrase, ct);

            // An encrypted key without a passphrase: ask once, when someone is there to answer
            if (failure is SshPassPhraseNullOrEmptyException && passphrase == null
                && PromptPassphrase?.Invoke() is { Length: > 0 } typed)
            {
                passphrase = typed;
                failure = await TryConnectAsync(ssh, target, passphrase, ct);
            }

            if (failure != null)
            {
                ssh.Dispose();
                var explained = ConnectionDiagnostics.Explain(failure, target.Host, target.Username, target.KeyPath);
                if (failure is SshPassPhraseNullOrEmptyException)
                    explained = explained with
                    {
                        Hint = $"Set the passphrase in the {TargetResolver.PassphraseVariable} environment variable, " +
                               "or pass --passphrase-env <VAR> / --passphrase-file <path>."
                    };
                AppLog.Warn($"Connection problem: {explained.Summary} | {explained.Detail}");
                throw new ConnectionFailedException(explained, target.Display);
            }

            _target = target with { Passphrase = passphrase };
            return _ssh = ssh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Exception?> TryConnectAsync(SshService ssh, ConnectionTarget target, string? passphrase, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => ssh.Connect(target.Host, target.Username, target.KeyPath, passphrase), ct);
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
