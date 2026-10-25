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

/// <summary>A validated request for a Let's Encrypt certificate (see <see cref="NginxInput.ParseCertificateRequest"/>).</summary>
public sealed record CertificateRequest(IReadOnlyList<string> Domains, string Name, string? Email);

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

    // Shell-quoted path of the certbot binary, found on first use
    private string? _certbot;

    public CertbotService(SshService ssh)
    {
        _ssh = ssh;
    }

    // snap installs to /snap/bin, which is only on PATH for login shells
    private async Task<string> FindPathAsync() =>
        (await _ssh.ExecuteAsync(
            "command -v certbot 2>/dev/null || { [ -x /snap/bin/certbot ] && echo /snap/bin/certbot; } || true")).Output.Trim();

    private async Task<string> CertbotAsync()
    {
        if (_certbot != null) return _certbot;
        var path = await FindPathAsync();
        if (path.Length == 0)
            throw OperationException.NotFound("certbot is not installed - install it with its nginx plugin first.");
        return _certbot = Shell.Quote(path);
    }

    public async Task<CertbotStatus> GetStatusAsync()
    {
        // Re-resolved on every status read: the desktop app may have switched to another server since
        var path = await FindPathAsync();
        _certbot = path.Length == 0 ? null : Shell.Quote(path);
        if (_certbot == null) return CertbotStatus.NotInstalled;

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
        var r = await _ssh.ExecuteAsync($"{await CertbotAsync()} certificates 2>&1");
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
        var certbot = await CertbotAsync();

        var command = dryRun
            ? $"{certbot} certonly {common} --dry-run 2>&1"
            : $"{certbot} run {common} {(redirect ? "--redirect" : "--no-redirect")} --keep-until-expiring 2>&1";
        return await _ssh.ExecuteStreamAsync(command, onLine);
    }

    /// <summary>Renews one certificate now, even if it is not due (counts against Let's Encrypt rate limits).</summary>
    public async Task<CommandResult> ForceRenewAsync(string certName, Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync(
            $"{await CertbotAsync()} {RenewNow} --cert-name {Shell.Quote(certName)} --force-renewal 2>&1", onLine);

    /// <summary>What the timer does: renews every certificate that is due, skips the rest.</summary>
    public async Task<CommandResult> RenewDueAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync($"{await CertbotAsync()} {RenewNow} 2>&1", onLine);

    /// <summary>Simulates renewal of every certificate against the staging server.</summary>
    public async Task<CommandResult> TestRenewalAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync($"{await CertbotAsync()} {RenewNow} --dry-run 2>&1", onLine);

    /// <summary>Config files under /etc/nginx that still point at this certificate's files.</summary>
    public async Task<List<string>> FindConfigsUsingAsync(string certName)
    {
        var dir = Shell.Quote(DeployPaths.CertLiveDir(certName) + "/");
        return (await _ssh.ExecuteAsync($"grep -rlF -- {dir} /etc/nginx 2>/dev/null; true")).Lines;
    }

    public async Task<CommandResult> DeleteAsync(string certName) =>
        await _ssh.ExecuteAsync($"{await CertbotAsync()} delete --non-interactive --cert-name {Shell.Quote(certName)} 2>&1");

    /// <summary>
    /// Deletes a certificate unless a config under /etc/nginx still points at its files: nginx refuses to
    /// reload (and to start after a reboot) when a certificate file is missing.
    /// </summary>
    public async Task DeleteIfUnusedAsync(string certName)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(certName));

        var users = await FindConfigsUsingAsync(certName);
        if (users.Count > 0)
            throw OperationException.Refused($"Certificate {certName} is still used by {string.Join(", ", users)}. " +
                                             "Delete those sites or remove their ssl_certificate lines first.");

        var r = await DeleteAsync(certName);
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }

    /// <summary>
    /// For certificate names read back from certbot (lineages like "example.com-0001"): only what would
    /// leave /etc/letsencrypt/live is rejected - the name is shell-quoted wherever it is used.
    /// </summary>
    public static string? ValidateExistingName(string certName) =>
        certName.Length > 0 && certName is not ("." or "..") && certName.IndexOfAny(['/', '\\']) < 0 && !certName.Any(char.IsControl)
            ? null
            : $"'{certName}' is not a certificate name.";

    /// <summary>
    /// certbot's installer puts the certificate into the enabled site whose server_name matches, so every
    /// domain needs one. Returns the domains no enabled site serves.
    /// </summary>
    public static List<string> UnservedDomains(IEnumerable<string> domains, IEnumerable<NginxSite> sites)
    {
        var enabled = sites.Where(s => s.IsEnabled).ToList();
        return domains.Where(d => !enabled.Any(s => s.Serves(d))).ToList();
    }

    /// <summary>
    /// Let's Encrypt validates over plain HTTP, so every name must resolve to this server. A name that
    /// does not resolve at all is certain to fail (and would count against the failed-validation rate
    /// limit), so it stops the request. An address mismatch only warns: behind NAT or a cloud public IP
    /// the public address never appears on the server's own interfaces.
    /// </summary>
    public async Task<bool> PreflightDnsAsync(IReadOnlyList<string> domains, Action<string> onLine)
    {
        onLine("→ Checking DNS...");
        var local = await ServerAddressesAsync();
        var ok = true;
        foreach (var domain in domains)
        {
            var addresses = await ResolveAsync(domain);
            if (addresses.Count == 0)
            {
                onLine($"✗ {domain} does not resolve - add an A/AAAA record that points to this server.");
                ok = false;
            }
            else if (local.Count > 0 && !addresses.Intersect(local).Any())
            {
                onLine($"⚠ {domain} → {string.Join(", ", addresses)}, but this server has {string.Join(", ", local)}. " +
                       "Fine behind NAT or a cloud public IP; otherwise the validation will fail.");
            }
            else
            {
                onLine($"✓ {domain} → {string.Join(", ", addresses)}");
            }
        }
        return ok;
    }

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
