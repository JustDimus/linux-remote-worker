using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>Where certbot is and whether it can do the nginx flow and renew by itself.</summary>
public sealed record CertbotStatus(
    string Path,
    string Version,
    bool HasNginxPlugin,
    bool AutoRenewActive,
    string AutoRenewText)
{
    public bool IsInstalled => Path.Length > 0;
    public static readonly CertbotStatus NotInstalled = new("", "", false, false, "");
}

/// <summary>
/// Let's Encrypt certificates through certbot's nginx plugin — the standard
/// "certbot --nginx -d example.com" flow: certbot answers the HTTP-01 challenge through nginx,
/// then adds the TLS lines (and optionally the HTTP→HTTPS redirect) to the matching site file.
/// Renewals run from certbot's own systemd timer and reload nginx automatically.
/// </summary>
public sealed class CertbotService
{
    // Without a TTY (as over SSH) certbot treats "renew" as a cron run and first sleeps up to
    // 8 minutes; renewals the user starts must run now. Supported since certbot 0.x.
    private const string RenewNow = "renew --non-interactive --no-random-sleep-on-renew";

    private readonly SshService _ssh;
    private string _certbot = "certbot";

    public CertbotService(SshService ssh)
    {
        _ssh = ssh;
    }

    public async Task<CertbotStatus> GetStatusAsync()
    {
        // snap installs to /snap/bin, which is only on PATH for login shells
        var path = (await _ssh.ExecuteAsync(
            "command -v certbot 2>/dev/null || { [ -x /snap/bin/certbot ] && echo /snap/bin/certbot; } || true")).Output.Trim();
        if (path.Length == 0) return CertbotStatus.NotInstalled;
        _certbot = Shell.Quote(path);

        var version = (await _ssh.ExecuteAsync($"{_certbot} --version 2>&1")).Text;
        var plugin = (await _ssh.ExecuteAsync($"{_certbot} plugins 2>/dev/null | grep -qx '\\* nginx'")).Succeeded;

        var timer = (await _ssh.ExecuteAsync(
            "for t in certbot.timer snap.certbot.renew.timer; do " +
            "if systemctl is-active --quiet \"$t\"; then " +
            "echo \"$t|$(systemctl show \"$t\" -p NextElapseUSecRealtime --value 2>/dev/null)\"; exit 0; fi; done; " +
            // Debian's cron job is a no-op whenever systemd runs, so it only counts without systemd
            "[ ! -d /run/systemd/system ] && [ -f /etc/cron.d/certbot ] && echo 'cron|'; true")).Output.Trim();

        var (active, text) = timer.Split('|') switch
        {
            ["cron", _] => (true, "Auto-renewal: cron job /etc/cron.d/certbot"),
            [var unit, var next] when unit.Length > 0 => (true,
                $"Auto-renewal: {unit} active" + (next.Trim().Length > 0 ? $", next run {next.Trim()}" : "")),
            _ => (false, "Auto-renewal is not scheduled - certificates will expire after 90 days")
        };

        return new CertbotStatus(path, version, plugin, active, text);
    }

    public async Task<CommandResult> InstallAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync(
            $"{Shell.AptInstall("certbot", "python3-certbot-nginx")} && (systemctl enable --now certbot.timer 2>&1 || true)",
            onLine);

    public async Task<CommandResult> EnableAutoRenewAsync() =>
        await _ssh.ExecuteAsync("systemctl enable --now certbot.timer 2>&1");

    public async Task<List<CertificateInfo>> ListCertificatesAsync()
    {
        var r = await _ssh.ExecuteAsync($"{_certbot} certificates 2>&1");
        return CertificateInfo.ParseCertbotOutput(r.Output);
    }

    /// <summary>
    /// Obtains (or re-installs) a certificate named <paramref name="certName"/> and wires it into the
    /// nginx site serving those domains. A dry run uses the staging server and changes nothing.
    /// </summary>
    public async Task<CommandResult> IssueAsync(
        IReadOnlyList<string> domains, string certName, string? email, bool redirect, bool dryRun, Action<string> onLine)
    {
        var account = string.IsNullOrWhiteSpace(email)
            ? "--register-unsafely-without-email"
            : $"-m {Shell.Quote(email.Trim())}";
        var names = string.Join(' ', domains.Select(d => $"-d {Shell.Quote(d)}"));
        var common = $"--nginx --non-interactive --agree-tos {account} --cert-name {Shell.Quote(certName)} {names}";

        var command = dryRun
            ? $"{_certbot} certonly {common} --dry-run 2>&1"
            : $"{_certbot} run {common} {(redirect ? "--redirect" : "--no-redirect")} --keep-until-expiring 2>&1";
        return await _ssh.ExecuteStreamAsync(command, onLine);
    }

    /// <summary>Renews one certificate now, even if it is not due (counts against Let's Encrypt rate limits).</summary>
    public async Task<CommandResult> ForceRenewAsync(string certName, Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync(
            $"{_certbot} {RenewNow} --cert-name {Shell.Quote(certName)} --force-renewal 2>&1", onLine);

    /// <summary>What the timer does: renews every certificate that is due, skips the rest.</summary>
    public async Task<CommandResult> RenewDueAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync($"{_certbot} {RenewNow} 2>&1", onLine);

    /// <summary>Simulates renewal of every certificate against the staging server.</summary>
    public async Task<CommandResult> TestRenewalAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync($"{_certbot} {RenewNow} --dry-run 2>&1", onLine);

    /// <summary>Config files under /etc/nginx that still point at this certificate's files.</summary>
    public async Task<List<string>> FindConfigsUsingAsync(string certName)
    {
        var dir = Shell.Quote(DeployPaths.CertLiveDir(certName) + "/");
        return (await _ssh.ExecuteAsync($"grep -rlF -- {dir} /etc/nginx 2>/dev/null; true")).Lines;
    }

    public async Task<CommandResult> DeleteAsync(string certName) =>
        await _ssh.ExecuteAsync($"{_certbot} delete --non-interactive --cert-name {Shell.Quote(certName)} 2>&1");

    /// <summary>Addresses a name resolves to, as seen from the server (A and AAAA).</summary>
    public async Task<List<string>> ResolveAsync(string host)
    {
        return (await _ssh.ExecuteAsync($"getent ahosts {Shell.Quote(host)} 2>/dev/null | awk '{{print $1}}' | sort -u")).Lines;
    }

    /// <summary>Global addresses configured on the server's interfaces.</summary>
    public async Task<List<string>> ServerAddressesAsync()
    {
        return (await _ssh.ExecuteAsync("ip -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1")).Lines;
    }
}
