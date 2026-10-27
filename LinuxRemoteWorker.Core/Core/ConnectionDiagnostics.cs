using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet.Common;

namespace LinuxRemoteWorker.Core;

/// <summary>A connection failure translated into something a user can act on.</summary>
public sealed record ConnectionProblem(string Summary, string Detail, string Hint)
{
    /// <summary>Everything about the failure, ready to paste into a bug report.</summary>
    public string ToClipboardText(SshCredentials credentials) =>
        new StringBuilder()
            .AppendLine("Linux Remote Worker - connection failure")
            .AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Target:  {credentials.Username}@{credentials.Host}")
            .AppendLine($"Login:   {credentials.AuthDescription}")
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
    public static ConnectionProblem? PreflightCheck(SshCredentials credentials)
    {
        var (host, username, privateKeyPath) = (credentials.Host, credentials.Username, credentials.KeyPath);
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

        if (credentials.Method == SshAuthMethod.Password)
            return string.IsNullOrEmpty(credentials.Password)
                ? new ConnectionProblem(
                    "Password is empty",
                    "Password login was chosen, but no password was entered.",
                    "Type the password of the Linux user, or switch to SSH key login.")
                : null;

        if (string.IsNullOrWhiteSpace(privateKeyPath))
            return new ConnectionProblem(
                "Private key is not selected",
                "SSH key login was chosen, but no private key file was picked.",
                "Click Browse and pick your private key file (id_rsa, id_ed25519 or a .pem file), or switch to password login.");

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
    public static ConnectionProblem Explain(Exception ex, SshCredentials credentials)
    {
        var (host, username, privateKeyPath) = (credentials.Host, credentials.Username, credentials.KeyPath);
        var detail = Flatten(ex);

        switch (ex)
        {
            case HostKeyChangedException changed:
                return new ConnectionProblem(
                    "The server's identity changed",
                    $"{host} presented the SSH host key {changed.Seen}, but {changed.Known.Key} was remembered for it " +
                    $"on {changed.Known.FirstSeenUtc:yyyy-MM-dd}. Nothing was sent to the server.",
                    "If the server was reinstalled or its SSH keys were regenerated, this is expected: confirm the new " +
                    "fingerprint with whoever runs the server (hosting console: `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`), " +
                    "then trust the new key. Otherwise someone may be intercepting the connection - do not log in.");

            case SessionRefusedException { PasswordExpired: true }:
                return new ConnectionProblem(
                    "The password has to be changed first",
                    detail,
                    $"The server accepts the password but requires a new one before anything else can run - common on new " +
                    $"hosting servers. Log in once in a terminal (Windows has ssh built in): ssh {username}@{host} - enter the " +
                    "current password, then the new one twice. Then connect here with the new password.");

            case SessionRefusedException:
                return new ConnectionProblem(
                    "The server accepted the login but closed the session",
                    detail,
                    $"Nothing can run as {username}. Usually the password has expired and must be changed in a terminal first " +
                    $"(ssh {username}@{host}); otherwise the account has no shell (nologin, an SFTP-only or git-only account, " +
                    "ForceCommand in sshd_config) - log in as another user.");

            // The server lists the methods it accepts; without "password" it only takes keys
            case SshAuthenticationException when credentials.Method == SshAuthMethod.Password && OnlyKeysAccepted(detail):
                return new ConnectionProblem(
                    "This server does not accept passwords",
                    detail,
                    $"{host} allows SSH key logins only (PasswordAuthentication is off in its sshd_config). " +
                    "Log in with a private key whose public half is in the user's ~/.ssh/authorized_keys.");

            case SshAuthenticationException when credentials.Method == SshAuthMethod.Password:
                return new ConnectionProblem(
                    "The server rejected the password",
                    detail,
                    $"Check the password (it is case-sensitive) and that user '{username}' exists on {host}. " +
                    "Root logins with a password are often forbidden (PermitRootLogin prohibit-password) - try the user " +
                    "the hosting provider gave you. Many failed attempts can get your IP blocked for a while (fail2ban).");

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

            // A wrong passphrase decrypts to garbage: OpenSSH keys then fail their check bytes,
            // PEM keys fail the cipher's padding check (a CryptographicException, not an SshException)
            case SshException when LooksLikeBadPassphrase(detail):
            case CryptographicException:
                return new ConnectionProblem(
                    "Wrong key passphrase, or an unsupported key format",
                    detail,
                    "Re-check the passphrase - it is case-sensitive. To test it outside the app, run " +
                    "'ssh-keygen -y -f <keyfile>': it asks for the passphrase and prints the public key when it is right.");

            case SshException when detail.Contains("private key", StringComparison.OrdinalIgnoreCase):
                return new ConnectionProblem(
                    "The private key file could not be read",
                    detail,
                    "The file does not look like an SSH private key. Supported are OpenSSH keys " +
                    "('-----BEGIN OPENSSH PRIVATE KEY-----'), PEM keys ('-----BEGIN RSA/EC PRIVATE KEY-----') " +
                    "and PuTTY .ppk files with RSA, ECDSA or Ed25519 keys; DSA keys are not supported.");

            default:
                return new ConnectionProblem(
                    "Could not connect",
                    detail,
                    $"See the application log for the full error: {AppLog.CurrentFile}");
        }
    }

    private static ConnectionProblem ExplainSocket(SocketException se, string host, string detail)
    {
        // A bare IPv6 literal is the single most common cause of "network unreachable":
        // the address is fine, the machine simply has no IPv6 route to it.
        var isIPv6Literal = IPAddress.TryParse(host, out var parsed)
                            && parsed.AddressFamily == AddressFamily.InterNetworkV6;

        return se.SocketErrorCode switch
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

            SocketError.NetworkUnreachable or SocketError.HostUnreachable
                or SocketError.NetworkDown or SocketError.AddressFamilyNotSupported
                when isIPv6Literal => new ConnectionProblem(
                "This computer has no IPv6 route to the server",
                detail,
                $"{host} is an IPv6 address, and this machine cannot reach the IPv6 internet" +
                (HasGlobalIPv6() ? "" : " (it currently has no global IPv6 address at all)") +
                ". Many home and office networks still provide IPv4 only, which is why the same server " +
                "may work from another computer. Enter the server's IPv4 address in HOST / IP instead - " +
                "your hosting panel lists both."),

            SocketError.NetworkUnreachable or SocketError.HostUnreachable
                or SocketError.NetworkDown => new ConnectionProblem(
                "The server is unreachable from this network",
                detail,
                $"No route to {host}. Check your internet connection or VPN."),

            _ => new ConnectionProblem(
                "Network error while connecting",
                detail,
                $"Socket error {se.SocketErrorCode} while reaching {host}:22.")
        };
    }

    /// <summary>True when this machine holds a routable (non link-local) IPv6 address.</summary>
    private static bool HasGlobalIPv6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                          && !a.Address.IsIPv6LinkLocal
                          && !a.Address.IsIPv6SiteLocal
                          && !IPAddress.IsLoopback(a.Address));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not inspect local IPv6 addresses: {ex.Message}");
            return true; // unknown - do not claim the machine lacks IPv6
        }
    }

    // SSH.NET reports e.g. "No suitable authentication method found to complete authentication (publickey)."
    private static bool OnlyKeysAccepted(string detail) =>
        detail.Contains("(publickey)", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBadPassphrase(string detail) =>
        detail.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("decrypt", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("padding", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("check bytes", StringComparison.OrdinalIgnoreCase);

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
