using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;
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

    /// <summary>How the live session logged in (the Security screen allows some changes only from a key login).</summary>
    public SshCredentials? Credentials { get; private set; }

    /// <summary>The host key the server presented on the last successful connect.</summary>
    public HostKey? ServerKey { get; private set; }

    /// <summary>True when <see cref="ServerKey"/> was seen for the first time and has just been remembered.</summary>
    public bool ServerKeyIsNew { get; private set; }

    /// <summary>Where server host keys are remembered and checked.</summary>
    public HostKeyStore HostKeys { get; init; } = HostKeyStore.Default;

    /// <summary>
    /// The login user is not root, so every command runs through `sudo -n` (passwordless sudo) and file
    /// transfers go through commands instead of SFTP, which can only reach what the user itself may read.
    /// </summary>
    public bool UsesSudo { get; private set; }

    /// <summary>Whether `sudo -n true` worked for a non-root login (always true for root).</summary>
    public bool SudoWorks { get; private set; } = true;

    /// <summary>"root", "sudo" (a user with passwordless sudo) or "none" (neither: changes will fail).</summary>
    public string Privileges => !UsesSudo ? "root" : SudoWorks ? "sudo" : "none";

    /// <summary>False when the server offers no SFTP; files then move through commands, as for sudo logins.</summary>
    public bool SftpAvailable { get; private set; }

    // Through sudo, or without SFTP, files are read and written by commands
    private bool CommandTransfers => UsesSudo || !SftpAvailable;

    /// <summary>
    /// Opens the SSH and SFTP channels. The server's host key must match the one remembered for it; an
    /// unknown server's key is remembered once the login succeeded. Throws <see cref="HostKeyChangedException"/>
    /// when the key changed, <see cref="SessionRefusedException"/> when the login works but no command can run
    /// (an expired password, an account without a shell).
    /// </summary>
    public void Connect(SshCredentials credentials)
    {
        // Release the previous session's clients instead of only disconnecting them
        Dispose();
        _ssh = null;
        _sftp = null;
        Credentials = null;
        ServerKey = null;
        ServerKeyIsNew = false;
        UsesSudo = false;
        SudoWorks = true;
        SftpAvailable = false;

        var (host, username) = (credentials.Host, credentials.Username);
        AppLog.Info($"Connecting to {username}@{host}:22 using {credentials.AuthDescription}" +
                    (credentials.Method == SshAuthMethod.Key ? (credentials.Passphrase == null ? " (no passphrase)" : " (with passphrase)") : ""));

        var connectionInfo = new ConnectionInfo(host, username, AuthenticationMethods(credentials)) { Timeout = ConnectTimeout };

        // Read before dialling out, so a damaged known_hosts.json fails here with its own explanation
        var known = HostKeys.Lookup(host);
        HostKey? seen = null;
        HostKeyChangedException? changed = null;
        void CheckHostKey(object? sender, HostKeyEventArgs e)
        {
            seen = new HostKey(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
            e.CanTrust = known == null || (known.Algorithm == seen.Algorithm && known.Fingerprint == seen.Fingerprint);
            if (!e.CanTrust) changed = new HostKeyChangedException(host, known!, seen);
        }

        _ssh = new SshClient(connectionInfo);
        _sftp = new SftpClient(connectionInfo);
        _ssh.HostKeyReceived += CheckHostKey;
        _sftp.HostKeyReceived += CheckHostKey;

        string probeOutput;
        try
        {
            _ssh.Connect();
            AppLog.Info("SSH channel established");
            // A command first: it tells an expired password or a shell-less account from a server without SFTP
            probeOutput = RunProbe(username, host);
            ConnectSftp();
        }
        catch (Exception ex)
        {
            Disconnect();
            if (changed != null)
            {
                AppLog.Error($"SSH connect refused: {changed.Message}");
                throw changed;
            }
            AppLog.Error($"SSH connect failed: {username}@{host}", ex);
            throw;
        }

        // Remembered only after the login worked, so a mistyped address leaves nothing behind
        if (seen != null && known == null)
        {
            HostKeys.Remember(host, seen);
            ServerKeyIsNew = true;
        }

        Host = host;
        Username = username;
        Credentials = credentials;
        ServerKey = seen;
        DetectPrivileges(probeOutput);
        AppLog.Info($"SSH connected: {username}@{host}, host key {seen}, privileges: {Privileges}" +
                    (SftpAvailable ? "" : ", no SFTP (files move through commands)"));
    }

    private const string ProbeCommand = "id -u; sudo -n true 2>/dev/null && echo sudo-ok";

    // Runs the first command of the session; a session the server closes at once becomes a SessionRefusedException
    private string RunProbe(string username, string host)
    {
        string output, error;
        int? exit;
        try
        {
            using var probe = _ssh!.CreateCommand(ProbeCommand);
            probe.CommandTimeout = ConnectTimeout;
            output = probe.Execute();
            (error, exit) = (probe.Error, probe.ExitStatus);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException)
        {
            throw SessionRefusedException.From(username, host, "", ex.Message, ex);
        }

        var uid = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (uid == null || !uid.All(char.IsAsciiDigit))
            throw SessionRefusedException.From(username, host, error, exit is { } code ? $"exit {code}" : "no exit status", null);
        return output;
    }

    // An SFTP server is optional: without one, file transfers fall back to commands
    private void ConnectSftp()
    {
        try
        {
            _sftp!.Connect();
            SftpAvailable = true;
            AppLog.Info("SFTP channel established");
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException)
        {
            AppLog.Warn($"SFTP is not available ({ex.Message}) - files move through commands instead");
            try { _sftp?.Dispose(); } catch { /* already unusable */ }
            _sftp = null;
        }
    }

    // Root runs commands as they are; anyone else through passwordless sudo, which is checked once here
    private void DetectPrivileges(string probeOutput)
    {
        var lines = probeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        UsesSudo = lines.FirstOrDefault() != "0";
        SudoWorks = !UsesSudo || lines.Contains("sudo-ok");
        if (!SudoWorks)
            AppLog.Warn($"{Username} is not root and has no passwordless sudo - commands that change the server will fail");
    }

    /// <summary>The command as it must run: through sudo for a non-root login, otherwise unchanged.</summary>
    public string Elevate(string command) => UsesSudo ? SudoCommand(command) : command;

    /// <summary>`sudo -n -H -- bash -c '…'`: no password prompt (fails instead), root's HOME, the whole script quoted.</summary>
    public static string SudoCommand(string command) => $"sudo -n -H -- bash -c {Shell.Quote(command)}";

    private const string NoSudoHint =
        " (this login is not root and has no passwordless sudo - log in as root, or as a user created on the Security screen)";

    private static AuthenticationMethod[] AuthenticationMethods(SshCredentials credentials)
    {
        if (credentials.Method == SshAuthMethod.Password)
        {
            var password = credentials.Password ?? string.Empty;
            // Many servers ask for the password through keyboard-interactive (PAM) instead of "password"
            var interactive = new KeyboardInteractiveAuthenticationMethod(credentials.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts.Where(p => !p.IsEchoed))
                    prompt.Response = password;
            };
            return [new PasswordAuthenticationMethod(credentials.Username, password), interactive];
        }

        PrivateKeyFile keyFile;
        try
        {
            keyFile = credentials.Passphrase != null
                ? new PrivateKeyFile(credentials.KeyPath, credentials.Passphrase)
                : new PrivateKeyFile(credentials.KeyPath);
            AppLog.Info("Private key loaded");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Private key could not be loaded: {credentials.KeyPath}", ex);
            throw;
        }
        return [new PrivateKeyAuthenticationMethod(credentials.Username, keyFile)];
    }

    /// <summary>
    /// Logs in once more with <paramref name="credentials"/> on a separate connection and closes it - proof that a
    /// new login works (with a new key, or after sshd was reconfigured) while this session stays open. For a
    /// non-root user it also proves that passwordless sudo works. Returns null when it worked, otherwise the failure.
    /// </summary>
    public async Task<Exception?> TryLoginAsync(SshCredentials credentials)
    {
        using var probe = new SshService { HostKeys = HostKeys };
        try
        {
            await Task.Run(() => probe.Connect(credentials));
            if (!probe.SudoWorks)
                return new InvalidOperationException($"{credentials.Username} logged in, but has no passwordless sudo.");
            var r = await probe.ExecuteAsync("true");
            return r.Succeeded ? null : new InvalidOperationException($"A test command failed after logging in: {r.Text}");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public string RunCommand(string command)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        AppLog.Info($"$ {command}");
        using var cmd = _ssh.CreateCommand(Elevate(command));
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
    /// <param name="timeout">Gives up (SshOperationTimeoutException) when the command runs longer; no limit when null.</param>
    /// <param name="asLoginUser">Run as the login user even when commands normally go through sudo.</param>
    public async Task<CommandResult> ExecuteAsync(string command, string? logAs = null, TimeSpan? timeout = null, bool asLoginUser = false)
    {
        var client = ConnectedClient();
        return await Task.Run(() =>
        {
            AppLog.Info($"$ {logAs ?? command}");
            using var cmd = client.CreateCommand(asLoginUser ? command : Elevate(command));
            if (timeout is { } limit) cmd.CommandTimeout = limit;
            cmd.Execute();
            return LogResult(WithSudoHint(new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error)));
        });
    }

    // "sudo: a password is required" means the login cannot do anything as root - say so where it shows up
    private CommandResult WithSudoHint(CommandResult result) =>
        UsesSudo && !SudoWorks && !result.Succeeded && result.Error.Contains("sudo", StringComparison.Ordinal)
            ? result with { Error = result.Error.TrimEnd() + NoSudoHint }
            : result;

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
            using var cmd = client.CreateCommand(Elevate(command));
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
            return LogResult(WithSudoHint(new CommandResult(cmd.ExitStatus ?? -1, string.Join("\n", tail), cmd.Error)));
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
        if (CommandTransfers)
        {
            await DownloadWithSudoAsync(remotePath, localPath);
            return;
        }
        var sftp = ConnectedSftp();

        await Task.Run(() =>
        {
            // Checked first, so a wrong path does not leave an empty file behind on this PC
            if (!sftp.Exists(remotePath))
                throw OperationException.NotFound($"{remotePath} does not exist on the server.");

            AppLog.Info($"Downloading {remotePath} -> {localPath}");
            try
            {
                using var fs = File.Create(localPath);
                sftp.DownloadFile(remotePath, fs);
            }
            catch
            {
                try { File.Delete(localPath); } catch { /* keep the original error */ }
                throw;
            }
        });
    }

    // Through sudo the file comes as base64, line by line - text the command stream carries reliably
    private async Task DownloadWithSudoAsync(string remotePath, string localPath)
    {
        if (!await ExistsAsync(remotePath))
            throw OperationException.NotFound($"{remotePath} does not exist on the server.");

        AppLog.Info($"Downloading {remotePath} -> {localPath} (through sudo)");
        try
        {
            await using var file = File.Create(localPath);
            var r = await ExecuteStreamAsync($"set -o pipefail; cat -- {Shell.Quote(remotePath)} | base64 -w 76",
                line => file.Write(Convert.FromBase64String(line.Trim())));
            if (!r.Succeeded) throw OperationException.Failed($"Could not read {remotePath}.", r.Error);
        }
        catch
        {
            try { File.Delete(localPath); } catch { /* keep the original error */ }
            throw;
        }
    }

    /// <summary>Reads a (small) remote file into memory.</summary>
    public async Task<byte[]> ReadBytesAsync(string remotePath)
    {
        if (CommandTransfers)
        {
            var r = await ExecuteAsync($"base64 -w 0 < {Shell.Quote(remotePath)}");
            if (!r.Succeeded) throw OperationException.Failed($"Could not read {remotePath}.", r.Error);
            return Convert.FromBase64String(r.Output.Trim());
        }
        var sftp = ConnectedSftp();
        return await Task.Run(() =>
        {
            using var buffer = new MemoryStream();
            sftp.DownloadFile(remotePath, buffer);
            return buffer.ToArray();
        });
    }

    /// <summary>Uploads a local file. Without <paramref name="overwrite"/> an existing remote file is an error.</summary>
    public async Task UploadFileAsync(string localPath, string remotePath, bool overwrite)
    {
        if (CommandTransfers)
        {
            await UploadWithSudoAsync(localPath, remotePath, overwrite);
            return;
        }
        var sftp = ConnectedSftp();

        await Task.Run(() =>
        {
            AppLog.Info($"Uploading {localPath} -> {remotePath}" + (overwrite ? " (overwrite)" : ""));
            using var fs = File.OpenRead(localPath);
            sftp.UploadFile(fs, remotePath, canOverride: overwrite);
        });
    }

    public const int ExitFileExists = 17;

    // The file's bytes go to the command's stdin; an existing file keeps its owner and mode, like an SFTP overwrite
    private async Task UploadWithSudoAsync(string localPath, string remotePath, bool overwrite)
    {
        var client = ConnectedClient();
        var script = UploadScript(remotePath, overwrite);

        AppLog.Info($"Uploading {localPath} -> {remotePath} (through sudo)" + (overwrite ? " (overwrite)" : ""));
        var result = await Task.Run(async () =>
        {
            using var cmd = client.CreateCommand(Elevate(script));
            var running = cmd.ExecuteAsync();
            await using (var input = cmd.CreateInputStream())
            await using (var source = File.OpenRead(localPath))
                await source.CopyToAsync(input);
            await running;
            return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
        });
        if (result.ExitCode == ExitFileExists)
            throw OperationException.Invalid($"{remotePath} already exists on the server.");
        if (!LogResult(WithSudoHint(result)).Succeeded)
            throw OperationException.Failed($"Could not write {remotePath}.", result.Text);
    }

    /// <summary>Writes stdin to <paramref name="remotePath"/>: an existing file is overwritten in place, a new one is mode 644.</summary>
    internal static string UploadScript(string remotePath, bool overwrite)
    {
        var f = Shell.Quote(remotePath);
        return (overwrite ? "" : $"[ -e {f} ] && exit {ExitFileExists}; ") +
               $"if [ -e {f} ]; then cat > {f}; else install -m 644 /dev/null {f} && cat > {f}; fi";
    }

    public async Task<bool> ExistsAsync(string remotePath)
    {
        if (CommandTransfers) return (await ExecuteAsync($"test -e {Shell.Quote(remotePath)}")).Succeeded;
        var sftp = ConnectedSftp();
        return await Task.Run(() => sftp.Exists(remotePath));
    }

    public async Task<IEnumerable<string>> ListDirectoryAsync(string remotePath)
    {
        if (CommandTransfers) return (await ListWithSudoAsync(remotePath)).Select(f => f.FullName);
        var sftp = ConnectedSftp();

        return await Task.Run(() =>
            sftp.ListDirectory(remotePath)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => f.FullName));
    }

    /// <summary>Directory entries with type, size and modification time; directories first, then by name.</summary>
    public async Task<List<RemoteFileInfo>> ListDirectoryDetailedAsync(string remotePath)
    {
        if (CommandTransfers)
            return (await ListWithSudoAsync(remotePath))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
        var sftp = ConnectedSftp();

        return await Task.Run(() =>
            sftp.ListDirectory(remotePath)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new RemoteFileInfo(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList());
    }

    private async Task<List<RemoteFileInfo>> ListWithSudoAsync(string remotePath)
    {
        var r = await ExecuteAsync(RemoteListing.Command(remotePath));
        if (!r.Succeeded) throw OperationException.Failed($"Could not list {remotePath}.", r.Text);
        return RemoteListing.Parse(remotePath, r.Output);
    }

    private SftpClient ConnectedSftp() =>
        _sftp is { IsConnected: true } client ? client : throw new InvalidOperationException("Not connected");

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
