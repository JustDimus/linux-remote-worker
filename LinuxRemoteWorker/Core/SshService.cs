using System.IO;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace LinuxRemoteWorker.Core;

public class SshService : IDisposable
{
    private SshClient? _ssh;
    private SftpClient? _sftp;

    public bool IsConnected => _ssh?.IsConnected == true;
    public string? Host { get; private set; }
    public string? Username { get; private set; }

    public void Connect(string host, string username, string privateKeyPath, string? passphrase = null)
    {
        Disconnect();

        var keyFile = passphrase != null
            ? new PrivateKeyFile(privateKeyPath, passphrase)
            : new PrivateKeyFile(privateKeyPath);

        var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
        var connectionInfo = new ConnectionInfo(host, username, authMethod);

        _ssh = new SshClient(connectionInfo);
        _sftp = new SftpClient(connectionInfo);

        _ssh.Connect();
        _sftp.Connect();

        Host = host;
        Username = username;
    }

    public string RunCommand(string command)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        using var cmd = _ssh.CreateCommand(command);
        var result = cmd.Execute();
        if (cmd.ExitStatus != 0 && !string.IsNullOrEmpty(cmd.Error))
            return cmd.Error.Trim();
        return result.Trim();
    }

    public async Task<string> RunCommandAsync(string command)
    {
        return await Task.Run(() => RunCommand(command));
    }

    public async Task<string> RunCommandStreamAsync(string command, Action<string> onLine, CancellationToken ct = default)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            using var cmd = _ssh.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();
            using var reader = new StreamReader(cmd.OutputStream);
            while (!asyncResult.IsCompleted || !reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (line != null)
                    onLine(line);
            }
            cmd.EndExecute(asyncResult);
            return cmd.Result.Trim();
        }, ct);
    }

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
        _sftp?.Disconnect();
        _ssh?.Disconnect();
    }

    public void Dispose()
    {
        Disconnect();
        _sftp?.Dispose();
        _ssh?.Dispose();
    }
}
