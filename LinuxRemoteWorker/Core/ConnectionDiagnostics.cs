using System.IO;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet.Common;

namespace LinuxRemoteWorker.Core;

/// <summary>A connection failure translated into something a user can act on.</summary>
public sealed record ConnectionProblem(string Summary, string Detail, string Hint)
{
    /// <summary>Everything about the failure, ready to paste into a bug report.</summary>
    public string ToClipboardText(string host, string username, string keyPath) =>
        new StringBuilder()
            .AppendLine("Linux Remote Worker - connection failure")
            .AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Target:  {username}@{host}")
            .AppendLine($"Key:     {keyPath}")
            .AppendLine($"Problem: {Summary}")
            .AppendLine($"Detail:  {Detail}")
            .AppendLine($"Hint:    {Hint}")
            .AppendLine($"Log:     {AppLog.CurrentFile}")
            .ToString();
}

/// <summary>
/// Turns SSH failures (and the cryptic exceptions behind them) into a plain
/// explanation plus a concrete next step, so the connect screen can show
/// the actual reason instead of a raw exception message.
/// </summary>
public static class ConnectionDiagnostics
{
    /// <summary>
    /// Checks what can be checked before dialling out. Returns null when the input looks usable.
    /// </summary>
    public static ConnectionProblem? PreflightCheck(string host, string username, string privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new ConnectionProblem(
                "Host is empty",
                "No server address was entered.",
                "Enter the server IP address or hostname in the HOST / IP field.");

        if (string.IsNullOrWhiteSpace(username))
            return new ConnectionProblem(
                "Username is empty",
                "No SSH user was entered.",
                "Enter the Linux user to log in as, for example 'root' or 'ubuntu'.");

        if (string.IsNullOrWhiteSpace(privateKeyPath))
            return new ConnectionProblem(
                "Private key is not selected",
                "This app authenticates with an SSH private key, not a password.",
                "Click Browse and pick your private key file (id_rsa, id_ed25519 or a .pem file).");

        if (!File.Exists(privateKeyPath))
            return new ConnectionProblem(
                "Private key file not found",
                $"There is no file at: {privateKeyPath}",
                "Check the path - the key may have been moved, renamed, or the profile was saved on another machine.");

        if (privateKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            return new ConnectionProblem(
                "That is a public key, not a private key",
                $"{Path.GetFileName(privateKeyPath)} ends with .pub, which is the key you upload to the server.",
                "Select the matching private key - the same file name without the .pub extension.");

        try
        {
            if (new FileInfo(privateKeyPath).Length == 0)
                return new ConnectionProblem(
                    "Private key file is empty",
                    $"The file {privateKeyPath} contains no data.",
                    "Select the private key file that actually holds the key material.");
        }
        catch (Exception ex)
        {
            return new ConnectionProblem(
                "Private key file is not readable",
                ex.Message,
                "Check that you have permission to read the key file.");
        }

        return null;
    }

    /// <summary>Explains an exception thrown while connecting.</summary>
    public static ConnectionProblem Explain(Exception ex, string host, string username, string privateKeyPath)
    {
        var detail = Flatten(ex);

        switch (ex)
        {
            case SshAuthenticationException:
                return new ConnectionProblem(
                    "The server rejected the key",
                    detail,
                    $"The server answered, but refused the login. Check that the matching public key is in " +
                    $"~/.ssh/authorized_keys of user '{username}' on {host}, and that the username matches " +
                    "the account the key belongs to.");

            case SshOperationTimeoutException:
                return new ConnectionProblem(
                    "The server did not answer in time",
                    detail,
                    $"No SSH reply from {host}:22. The machine may be off, or a firewall / cloud security " +
                    $"group is dropping port 22. Try 'ssh {username}@{host}' in a terminal to confirm.");

            case SocketException se:
                return ExplainSocket(se, host, detail);

            case ProxyException:
                return new ConnectionProblem(
                    "Proxy error",
                    detail,
                    "A proxy sits between you and the server and refused the tunnel. Check your proxy settings.");

            case SshConnectionException:
                return new ConnectionProblem(
                    "The SSH connection broke during the handshake",
                    detail,
                    $"Something answered on {host}:22 but the SSH handshake failed. The server may be " +
                    "restarting, rate-limiting you (fail2ban), or the port is used by a different service.");

            case FileNotFoundException:
            case DirectoryNotFoundException:
                return new ConnectionProblem(
                    "Private key file not found",
                    detail,
                    $"Check the path: {privateKeyPath}");

            case UnauthorizedAccessException:
                return new ConnectionProblem(
                    "No permission to read the private key",
                    detail,
                    $"Windows refused access to {privateKeyPath}. Check the file's security settings.");

            case SshPassPhraseNullOrEmptyException:
                return new ConnectionProblem(
                    "The private key is encrypted and needs a passphrase",
                    detail,
                    "Type the key's passphrase into the PASSPHRASE field and connect again.");

            case SshException when LooksLikeBadPassphrase(detail):
                return new ConnectionProblem(
                    "Wrong key passphrase, or an unsupported key format",
                    detail,
                    "Re-check the passphrase. If the key starts with '-----BEGIN OPENSSH PRIVATE KEY-----' " +
                    "and keeps failing, convert it to PEM: ssh-keygen -p -m PEM -f <keyfile>");

            case SshException when detail.Contains("private key", StringComparison.OrdinalIgnoreCase):
                return new ConnectionProblem(
                    "The private key file could not be read",
                    detail,
                    "The file does not look like an SSH private key. It must start with " +
                    "'-----BEGIN ... PRIVATE KEY-----'. A PuTTY .ppk file will not work - export it " +
                    "first with PuTTYgen: Conversions -> Export OpenSSH key.");

            default:
                return new ConnectionProblem(
                    "Could not connect",
                    detail,
                    $"See the application log for the full error: {AppLog.CurrentFile}");
        }
    }

    private static ConnectionProblem ExplainSocket(SocketException se, string host, string detail) => se.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData => new ConnectionProblem(
            "Host name could not be resolved",
            detail,
            $"DNS does not know '{host}'. Check the spelling, or use the server's IP address instead."),

        SocketError.ConnectionRefused => new ConnectionProblem(
            "Connection refused on port 22",
            detail,
            $"{host} is reachable but nothing is listening on port 22. Make sure sshd runs on the server " +
            "('systemctl status ssh'), or that SSH is not on a custom port."),

        SocketError.TimedOut => new ConnectionProblem(
            "Connection timed out",
            detail,
            $"No answer from {host}:22. Usually a firewall, a cloud security group that does not allow " +
            "your IP, or a VPN that is not connected."),

        SocketError.NetworkUnreachable or SocketError.HostUnreachable => new ConnectionProblem(
            "The server is unreachable from this network",
            detail,
            $"No route to {host}. Check your internet connection or VPN."),

        _ => new ConnectionProblem(
            "Network error while connecting",
            detail,
            $"Socket error {se.SocketErrorCode} while reaching {host}:22.")
    };

    private static bool LooksLikeBadPassphrase(string detail) =>
        detail.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("decrypt", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("padding", StringComparison.OrdinalIgnoreCase);

    /// <summary>Flattens nested exception messages into one readable line.</summary>
    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message.Trim();
            if (msg.Length > 0 && !parts.Contains(msg)) parts.Add(msg);
        }
        return string.Join(" -> ", parts);
    }
}
