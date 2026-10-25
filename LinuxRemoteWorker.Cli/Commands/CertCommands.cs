using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Nginx;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("certs", "Let's Encrypt certificates through certbot's nginx plugin: issue, list, renew, delete")]
public sealed class CertCommands : CommandSet
{
    public CertCommands(CommandContext context) : base(context) { }

    private async Task<(CertbotService Certbot, NginxService Nginx)> ServicesAsync()
    {
        var ssh = await SshAsync();
        return (new CertbotService(ssh), new NginxService(ssh));
    }

    [Command("status", "Whether certbot and its nginx plugin are installed, and whether automatic renewal is scheduled.", ReadOnly = true)]
    public async Task<object> Status()
    {
        var status = await new CertbotService(await SshAsync()).GetStatusAsync();
        return new
        {
            installed = status.IsInstalled,
            status.Path,
            status.Version,
            status.HasNginxPlugin,
            ready = status.IsInstalled && status.HasNginxPlugin,
            status.AutoRenewActive,
            autoRenew = status.AutoRenewText
        };
    }

    [Command("install", "Install certbot with its nginx plugin (apt) and enable the renewal timer.", Streams = true, Idempotent = true)]
    public async Task<Outcome> Install()
    {
        var certbot = new CertbotService(await SshAsync());
        var r = await certbot.InstallAsync(Lines);
        if (!r.Succeeded) throw OperationException.Failed($"Installing certbot failed (exit {r.ExitCode}) - see the output.", r.Text);
        return new Outcome("certbot installed") { Data = await certbot.GetStatusAsync() };
    }

    [Command("list", "Certificates with domains, expiry, days left, health (ok / soon = inside the 30-day renewal window / bad) and the sites using each.",
        ReadOnly = true)]
    public async Task<List<object>> List()
    {
        var (certbot, nginx) = await ServicesAsync();
        List<NginxSite> sites = await nginx.IsInstalledAsync() ? await nginx.ListSitesAsync() : [];
        return (await certbot.ListCertificatesAsync()).Select(c => CertDto(c.WithUsers(sites))).ToList();
    }

    [Command("issue", "Request a certificate and install it into the enabled site(s) serving those domains, redirecting HTTP to HTTPS. " +
                      "Every domain must resolve to this server (checked first). --dry-run tests against the staging server and changes nothing.",
        Streams = true, Example = "certs issue --domains \"shop.example.com www.shop.example.com\" --email admin@example.com")]
    public async Task<Outcome> Issue(
        [Arg("Public domain names, space or comma separated (no wildcards)")] string domains,
        [Arg("Certificate name (default: the first domain)")] string? name = null,
        [Arg("Account e-mail for expiry notices (optional)")] string? email = null,
        [Arg("Redirect HTTP to HTTPS")] bool redirect = true,
        [Arg("Test against the Let's Encrypt staging server; nothing changes")] bool dryRun = false)
    {
        var (request, error) = NginxInput.ParseCertificateRequest(domains, name, email);
        if (request == null) throw OperationException.Invalid(error!);
        return await IssueAsync(request, redirect, dryRun);
    }

    [Command("secure-site", "Turn on HTTPS for a site: a certificate for its public server_names, installed into it, with HTTP redirected. The site must be enabled.",
        Streams = true, Example = "certs secure-site shop.example.com --email admin@example.com")]
    public async Task<Outcome> SecureSite(
        [Arg("Site name (see nginx sites list)", Positional = true)] string site,
        [Arg("Account e-mail for expiry notices (optional)")] string? email = null,
        [Arg("Redirect HTTP to HTTPS")] bool redirect = true,
        [Arg("Test against the Let's Encrypt staging server; nothing changes")] bool dryRun = false)
    {
        var (_, nginx) = await ServicesAsync();
        var found = (await nginx.ListSitesAsync()).FirstOrDefault(s => s.Name == site)
                    ?? throw OperationException.NotFound($"No site named {site}.");
        if (!found.IsEnabled)
            throw OperationException.Invalid($"Enable site {site} first - certbot installs the certificate into an enabled site.");
        if (found.CertificateDomains.Count == 0)
            throw OperationException.Invalid($"Site {site} has no public domain name in server_name - a certificate needs one.");

        var (request, error) = NginxInput.ParseCertificateRequest(string.Join(' ', found.CertificateDomains), found.Name, email);
        if (request == null) throw OperationException.Invalid(error!);
        return await IssueAsync(request, redirect, dryRun);
    }

    [Command("renew", "Renew one certificate now even if it is not due. certbot already renews ~30 days before expiry, and Let's Encrypt allows 5 renewals of the same names per week.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> Renew([Arg("Certificate name (see certs list)", Positional = true)] string name)
    {
        OperationException.ThrowIfInvalid(CertbotService.ValidateExistingName(name));
        var (certbot, _) = await ServicesAsync();
        var r = await certbot.ForceRenewAsync(name, Lines);
        if (!r.Succeeded) throw OperationException.Failed($"Renewing {name} failed - see the output.", r.Text);
        return new Outcome($"Certificate {name} renewed and nginx reloaded");
    }

    [Command("renew-due", "What the renewal timer does: renew every certificate that is due, skip the rest.", Streams = true, Idempotent = true)]
    public async Task<Outcome> RenewDue()
    {
        var (certbot, _) = await ServicesAsync();
        var r = await certbot.RenewDueAsync(Lines);
        if (!r.Succeeded) throw OperationException.Failed("Renewal failed - see the output.", r.Text);
        return new Outcome("Renewal run finished - certificates that were due are renewed");
    }

    [Command("test-renewal", "Simulate renewing every certificate against the staging server - proves automatic renewal will work.",
        ReadOnly = true, Streams = true)]
    public async Task<Outcome> TestRenewal()
    {
        var (certbot, _) = await ServicesAsync();
        var r = await certbot.TestRenewalAsync(Lines);
        if (!r.Succeeded)
            throw OperationException.Failed("The renewal dry run failed, so automatic renewal would fail too - see the output.", r.Text);
        return new Outcome("Renewal dry run passed - automatic renewal will work");
    }

    [Command("delete", "Delete a certificate. Refused while any nginx config still points at it (nginx would fail to start).", Destructive = true)]
    public async Task<Outcome> Delete([Arg("Certificate name (see certs list)", Positional = true)] string name)
    {
        var (certbot, _) = await ServicesAsync();
        await certbot.DeleteIfUnusedAsync(name);
        return new Outcome($"Certificate {name} deleted");
    }

    [Command("auto-renew", "Enable and start certbot's renewal timer.", Idempotent = true)]
    public async Task<Outcome> AutoRenew()
    {
        var (certbot, _) = await ServicesAsync();
        var r = await certbot.EnableAutoRenewAsync();
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        var status = await certbot.GetStatusAsync();
        return new Outcome(status.AutoRenewText);
    }

    private async Task<Outcome> IssueAsync(CertificateRequest request, bool redirect, bool dryRun)
    {
        var (certbot, nginx) = await ServicesAsync();
        var status = await certbot.GetStatusAsync();
        if (!status.IsInstalled || !status.HasNginxPlugin)
            throw OperationException.Refused("Install certbot with its nginx plugin first: `lrw certs install`.");

        // certbot's installer puts the certificate into the enabled site whose server_name matches
        if (!dryRun)
        {
            var unserved = CertbotService.UnservedDomains(request.Domains, await nginx.ListSitesAsync());
            if (unserved.Count > 0)
                throw OperationException.Invalid($"No enabled site has server_name {string.Join(", ", unserved)}. " +
                                                 "Create or enable that site first, so certbot can install the certificate into it.");
        }

        if (!await certbot.PreflightDnsAsync(request.Domains, Lines))
            throw OperationException.Failed("Stopped before contacting Let's Encrypt: fix DNS first (see the output).");

        Lines(dryRun
            ? "→ Dry run against the Let's Encrypt staging server - nothing on this server changes..."
            : "→ Requesting the certificate from Let's Encrypt...");
        var r = await certbot.IssueAsync(request.Domains, request.Name, request.Email, redirect, dryRun, Lines);
        if (!r.Succeeded) throw OperationException.Failed("certbot failed - the reason is in the output.", r.Text);

        return new Outcome(dryRun
            ? "Dry run passed - the real request should succeed"
            : $"HTTPS is on for {string.Join(", ", request.Domains)}")
        {
            Data = new { certificate = request.Name, domains = request.Domains, dryRun, redirect }
        };
    }

    private static object CertDto(CertificateInfo c) => new
    {
        c.Name,
        c.Domains,
        expiry = c.Expiry,
        c.DaysLeft,
        c.Validity,
        c.Health,
        usedBy = c.UsedBy
    };
}
