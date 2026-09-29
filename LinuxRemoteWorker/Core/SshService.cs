using System.IO;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace LinuxRemoteWorker.Core;

public class SshService : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private const int StreamTailLines = 50;

    private SshClient? _ssh;
    private SftpClient? _sftp;

    public bool IsConnected => _ssh?.IsConnected == true;
    public string? Host { get; private set; }
    public string? Username { get; private set; }

    public void Connect(string host, string username, string privateKeyPath, string? passphrase = null)
    {
        // Release the previous session's clients instead of only disconnecting them
        Dispose();
        _ssh = null;
        _sftp = null;

        AppLog.Info($"Connecting to {username}@{host}:22 using key {privateKeyPath}" +
                    (passphrase == null ? " (no passphrase)" : " (with passphrase)"));

        PrivateKeyFile keyFile;
        try
        {
            keyFile = passphrase != null
                ? new PrivateKeyFile(privateKeyPath, passphrase)
                : new PrivateKeyFile(privateKeyPath);
            AppLog.Info("Private key loaded");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Private key could not be loaded: {privateKeyPath}", ex);
            throw;
        }

        var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
        var connectionInfo = new ConnectionInfo(host, username, authMethod)
        {
            Timeout = ConnectTimeout
        };

        _ssh = new SshClient(connectionInfo);
        _sftp = new SftpClient(connectionInfo);

        try
        {
            _ssh.Connect();
            AppLog.Info("SSH channel established");
            _sftp.Connect();
            AppLog.Info("SFTP channel established");
        }
        catch (Exception ex)
        {
            AppLog.Error($"SSH connect failed: {username}@{host}", ex);
            Disconnect();
            throw;
        }

        Host = host;
        Username = username;
        AppLog.Info($"SSH connected: {username}@{host}");
    }

    public string RunCommand(string command)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        AppLog.Info($"$ {command}");
        using var cmd = _ssh.CreateCommand(command);
        var result = cmd.Execute();

        if (cmd.ExitStatus != 0 && !string.IsNullOrEmpty(cmd.Error))
        {
            AppLog.Warn($"exit={cmd.ExitStatus} stderr: {Truncate(cmd.Error.Trim())}");
            return cmd.Error.Trim();
        }

        AppLog.Info($"exit={cmd.ExitStatus} out: {Truncate(result.Trim())}");
        return result.Trim();
    }

    private static string Truncate(string s, int max = 500)
        => s.Length <= max ? s : s[..max] + $"… (+{s.Length - max} chars)";

    public async Task<string> RunCommandAsync(string command)
    {
        return await Task.Run(() => RunCommand(command));
    }

    public async Task<string> RunCommandStreamAsync(string command, Action<string> onLine, CancellationToken ct = default)
        => (await ExecuteStreamAsync(command, onLine, ct)).Output.Trim();

    /// <summary>
    /// Runs a command and reports its exit code. Unlike <see cref="RunCommand"/>, a failure stays
    /// visible to the caller instead of being folded into the returned text.
    /// </summary>
    /// <param name="logAs">What to write to the app log instead of the command, when it carries a secret.</param>
    public async Task<CommandResult> ExecuteAsync(string command, string? logAs = null)
    {
        var client = ConnectedClient();
        return await Task.Run(() =>
        {
            AppLog.Info($"$ {logAs ?? command}");
            using var cmd = client.CreateCommand(command);
            cmd.Execute();
            return LogResult(new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error));
        });
    }

    /// <summary>
    /// Like <see cref="ExecuteAsync"/>, but hands every stdout line to <paramref name="onLine"/> as it
    /// arrives. The result's Output keeps only the last lines — where failures are reported.
    /// </summary>
    public async Task<CommandResult> ExecuteStreamAsync(string command, Action<string> onLine, CancellationToken ct = default)
    {
        var client = ConnectedClient();
        return await Task.Run(() =>
        {
            AppLog.Info($"$ {command}");
            using var cmd = client.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();
            using var reader = new StreamReader(cmd.OutputStream);
            var tail = new Queue<string>();
            while (!asyncResult.IsCompleted || !reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (line == null) continue;
                onLine(line);
                tail.Enqueue(line);
                if (tail.Count > StreamTailLines) tail.Dequeue();
            }
            cmd.EndExecute(asyncResult);
            return LogResult(new CommandResult(cmd.ExitStatus ?? -1, string.Join("\n", tail), cmd.Error));
        }, ct);
    }

    private SshClient ConnectedClient() =>
        _ssh is { IsConnected: true } client ? client : throw new InvalidOperationException("Not connected");

    // A failure explains itself at the end of the output, so that is the part worth logging
    private static CommandResult LogResult(CommandResult result)
    {
        if (result.Succeeded)
            AppLog.Info($"exit=0 out: {Truncate(result.Text)}");
        else
            AppLog.Warn($"exit={result.ExitCode} out: {TruncateStart(result.Text)}");
        return result;
    }

    private static string TruncateStart(string s, int max = 500)
        => s.Length <= max ? s : $"(… {s.Length - max} chars) " + s[^max..];

    public async Task DownloadFileAsync(string remotePath, string localPath)
    {
        if (_sftp == null || !_sftp.IsConnected)
            throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            using var fs = File.Create(localPath);
            _sftp.DownloadFile(remotePath, fs);
        });
    }

    public async Task<IEnumerable<string>> ListDirectoryAsync(string remotePath)
    {
        if (_sftp == null || !_sftp.IsConnected)
            throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
            _sftp.ListDirectory(remotePath)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => f.FullName));
    }

    public void Disconnect()
    {
        try { if (_sftp?.IsConnected == true) _sftp.Disconnect(); }
        catch (Exception ex) { AppLog.Warn($"SFTP disconnect failed: {ex.Message}"); }

        try { if (_ssh?.IsConnected == true) _ssh.Disconnect(); }
        catch (Exception ex) { AppLog.Warn($"SSH disconnect failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        Disconnect();
        _sftp?.Dispose();
        _ssh?.Dispose();
    }
}
