using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Nginx;

namespace LinuxRemoteWorker.Cli.Commands;

public enum SiteKindArg { Proxy, Static }

/// <summary>
/// Every change goes through NginxService's transaction: write → nginx -t → reload, or restore the
/// previous files when the test fails, so a mistake never takes the running sites down.
/// </summary>
[CommandGroup("nginx", "nginx: service, sites (reverse proxy / static), nginx.conf, logs - every change tested with nginx -t and rolled back on failure")]
public sealed class NginxCommands : CommandSet
{
    private const int MaxLogLines = 10000;

    public NginxCommands(CommandContext context) : base(context) { }

    private async Task<NginxService> NginxAsync() => new(await SshAsync());

    [Command("status", "Installed, version, systemd state, layout problems (missing includes), whether ufw blocks ports 80/443, IPv6, and the number of sites.",
        ReadOnly = true)]
    public async Task<object> Status()
    {
        var nginx = await NginxAsync();
        if (!await nginx.IsInstalledAsync()) return new { installed = false };

        var firewall = await nginx.GetFirewallStateAsync();
        return new
        {
            installed = true,
            version = await nginx.GetVersionAsync(),
            state = await nginx.GetServiceStateAsync(),
            layoutWarning = await nginx.CheckLayoutAsync(),
            firewall = new { firewall.UfwActive, firewall.AllowsHttp, firewall.AllowsHttps, firewall.BlocksWeb },
            ipv6 = await nginx.HasIpv6Async(),
            sites = (await nginx.ListSitesAsync()).Count
        };
    }

    [Command("install", "Install nginx (apt), enable and start it.", Streams = true, Idempotent = true)]
    public async Task<Outcome> Install()
    {
        var nginx = await NginxAsync();
        var r = await nginx.InstallAsync(Lines);
        if (!r.Succeeded) throw OperationException.Failed($"Installing nginx failed (exit {r.ExitCode}) - see the output.", r.Text);
        return new Outcome("nginx installed and started") { Data = new { version = await nginx.GetVersionAsync() } };
    }

    [Command("test", "Run nginx -t on the whole configuration. data.passed tells the result (exit code 1 when it fails).", ReadOnly = true)]
    public async Task<Outcome> Test()
    {
        var r = await (await NginxAsync()).TestConfigAsync();
        return new Outcome(r.Succeeded ? "Configuration test passed" : "Configuration test failed")
        {
            Data = new { passed = r.Succeeded, output = r.Text },
            ExitCode = r.Succeeded ? ExitCodes.Ok : ExitCodes.Failed
        };
    }

    [Command("reload", "Reload nginx (refused while nginx -t fails).", Idempotent = true)]
    public async Task<Outcome> Reload() => await ServiceActionAsync("reload");

    [Command("restart", "Restart nginx (refused while nginx -t fails).")]
    public async Task<Outcome> Restart() => await ServiceActionAsync("restart");

    [Command("start", "Start nginx (refused while nginx -t fails).", Idempotent = true)]
    public async Task<Outcome> Start() => await ServiceActionAsync("start");

    [Command("stop", "Stop nginx - every site on the server goes offline until it is started again.", Destructive = true)]
    public async Task<Outcome> Stop() => await ServiceActionAsync("stop");

    // ── Sites ──────────────────────────────────────────────────────────────

    [Command("sites list", "Every site in sites-available: enabled, domains, ports, target (proxy/static/redirect), HTTPS and certificates, and whether this tool created it.",
        ReadOnly = true)]
    public async Task<List<object>> SitesList() =>
        (await (await NginxAsync()).ListSitesAsync()).Select(SiteDto).ToList();

    [Command("sites show", "The config file of a site, exactly as stored.", ReadOnly = true)]
    public async Task<object> SitesShow([Arg("Site name (file in sites-available)", Positional = true)] string name)
    {
        OperationException.ThrowIfInvalid(NginxInput.ValidateExistingName(name));
        var nginx = await NginxAsync();
        var site = (await nginx.ListSitesAsync()).FirstOrDefault(s => s.Name == name)
                   ?? throw OperationException.NotFound($"No site named {name} in {DeployPaths.NginxSitesAvailable}.");
        var path = DeployPaths.SiteAvailablePath(name);
        return new { name, path, site.IsEnabled, content = await nginx.ReadFileAsync(path) };
    }

    [Command("sites create", "Create and enable a site on port 80: a reverse proxy to an upstream (or to a deployed service with --service), or static files. " +
                             "HTTPS is added afterwards with `certs secure-site`. Rolled back when nginx -t fails.",
        Example = "nginx sites create --domains \"shop.example.com www.shop.example.com\" --service shop-api")]
    public async Task<Outcome> SitesCreate(
        [Arg("Domain names (server_name), space or comma separated; an IP address or _ also works")] string domains,
        [Arg("proxy: forward to an app; static: serve files")] SiteKindArg kind = SiteKindArg.Proxy,
        [Arg("Site (file) name; default: the first domain")] string? name = null,
        [Arg("Proxy target such as http://127.0.0.1:5000")] string? upstream = null,
        [Arg("Proxy to this deployed service - its port is read from ASPNETCORE_URLS")] string? service = null,
        [Arg("Static root inside /var/www, /srv or /opt; default: /var/www/<name>", RemotePath = true)] string? root = null,
        [Arg("Proxy WebSocket/SignalR upgrades")] bool websockets = true,
        [Arg("Static: fall back to /index.html for client-side routes (single-page apps)")] bool spa = false,
        [Arg("client_max_body_size, e.g. 10m")] string maxBody = "10m")
    {
        var nginx = await NginxAsync();
        var siteKind = kind == SiteKindArg.Proxy ? SiteKind.ReverseProxy : SiteKind.StaticFiles;

        if (service != null)
        {
            if (siteKind != SiteKind.ReverseProxy) throw OperationException.Invalid("--service only applies to proxy sites.");
            var upstreams = await nginx.ListServiceUpstreamsAsync();
            upstream = upstreams.FirstOrDefault(u => u.AppName == service)?.Url
                       ?? throw OperationException.NotFound(
                           $"No deployed service '{service}' with ASPNETCORE_URLS. Known: {string.Join(", ", upstreams.Select(u => u.AppName))}");
        }
        if (siteKind == SiteKind.ReverseProxy && string.IsNullOrWhiteSpace(upstream))
            throw OperationException.Invalid("A proxy site needs --upstream http://127.0.0.1:<port> or --service <app>.");

        var (request, error) = NginxInput.BuildSiteRequest(domains, name, siteKind, upstream, root,
            websockets, spa, maxBody, await nginx.HasIpv6Async());
        if (request == null) throw OperationException.Invalid(error!);

        var r = await nginx.CreateSiteAsync(request);
        if (r.ExitCode == 4) throw OperationException.Refused(r.Text);
        var outcome = Change(r, $"Site {request.Name} created and enabled", $"Site {request.Name} was not created");
        return outcome with
        {
            Data = new { name = request.Name, path = DeployPaths.SiteAvailablePath(request.Name), domains = request.ServerNames, upstream = request.Upstream, root = siteKind == SiteKind.StaticFiles ? request.Root : null },
            Warning = await nginx.UpstreamWarningAsync(request)
        };
    }

    [Command("sites enable", "Enable a site (link it into sites-enabled) and reload; undone when nginx -t fails.", Idempotent = true)]
    public async Task<Outcome> SitesEnable([Arg("Site name", Positional = true)] string name) =>
        Change(await (await NginxAsync()).EnableSiteAsync(name), $"Site {name} enabled", $"Site {name} was not enabled");

    [Command("sites disable", "Disable a site (remove its sites-enabled links; the file stays) and reload.", Idempotent = true)]
    public async Task<Outcome> SitesDisable([Arg("Site name", Positional = true)] string name) =>
        Change(await (await NginxAsync()).DisableSiteAsync(name), $"Site {name} disabled - its file stays in sites-available", $"Site {name} was not disabled");

    [Command("sites delete", "Delete a site's file and links and reload. Web content and logs are kept. --delete-unused-certs also deletes certificates no other site uses.",
        Destructive = true)]
    public async Task<Outcome> SitesDelete(
        [Arg("Site name", Positional = true)] string name,
        [Arg("Also delete the site's certificates when nothing else uses them")] bool deleteUnusedCerts = false)
    {
        var nginx = await NginxAsync();
        var site = (await nginx.ListSitesAsync()).FirstOrDefault(s => s.Name == name);
        var outcome = Change(await nginx.DeleteSiteAsync(name), $"Site {name} deleted", $"Site {name} was not deleted");
        if (!deleteUnusedCerts || site == null || site.CertNames.Count == 0) return outcome;

        // A certificate nobody uses any more would otherwise keep renewing forever
        var certbot = new CertbotService(await SshAsync());
        var deleted = new List<string>();
        var kept = new List<string>();
        foreach (var cert in site.CertNames)
        {
            try
            {
                await certbot.DeleteIfUnusedAsync(cert);
                deleted.Add(cert);
            }
            catch (OperationException ex) when (ex.Kind == FailureKind.Refused)
            {
                kept.Add(cert);
            }
        }
        return outcome with { Data = new { certificatesDeleted = deleted, certificatesStillInUse = kept } };
    }

    [Command("sites edit", "Replace a site's config file. Saved only if the whole configuration passes nginx -t; otherwise the old file is put back. Read it first with sites show.",
        Unrestricted = true, Example = "nginx sites edit shop.example.com --content @shop.conf")]
    public async Task<Outcome> SitesEdit(
        [Arg("Site name", Positional = true)] string name,
        [Arg("The complete new file", AllowFile = true)] string content)
    {
        OperationException.ThrowIfInvalid(NginxInput.ValidateExistingName(name));
        var path = DeployPaths.SiteAvailablePath(name);
        return Change(await (await NginxAsync()).SaveFileAsync(path, content),
            $"Saved {path}; nginx -t passed and nginx was reloaded (a disabled site is only checked once enabled)", $"{path} was not saved");
    }

    [Command("conf show", "The global /etc/nginx/nginx.conf.", ReadOnly = true)]
    public async Task<object> ConfShow() =>
        new { path = DeployPaths.NginxConf, content = await (await NginxAsync()).ReadFileAsync(DeployPaths.NginxConf) };

    [Command("conf set", "Replace /etc/nginx/nginx.conf - kept only if nginx -t passes, otherwise restored.", Unrestricted = true)]
    public async Task<Outcome> ConfSet([Arg("The complete new nginx.conf", AllowFile = true)] string content) =>
        Change(await (await NginxAsync()).SaveFileAsync(DeployPaths.NginxConf, content),
            $"Saved {DeployPaths.NginxConf}; nginx -t passed and nginx was reloaded", $"{DeployPaths.NginxConf} was not saved");

    [Command("upstreams", "Deployed services that can be proxied, with the local URL taken from their ASPNETCORE_URLS.", ReadOnly = true)]
    public async Task<List<ServiceUpstream>> Upstreams() => await (await NginxAsync()).ListServiceUpstreamsAsync();

    [Command("open-firewall", "Allow ports 80 and 443 in ufw (plain port rules the firewall commands can list and delete).", Idempotent = true)]
    public async Task<Outcome> OpenFirewall()
    {
        var nginx = await NginxAsync();
        var r = await nginx.AllowWebInFirewallAsync();
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return new Outcome("Firewall: ports 80 and 443 are open") { Data = await nginx.GetFirewallStateAsync() };
    }

    [Command("logs list", "Log files in /var/log/nginx.", ReadOnly = true)]
    public async Task<List<string>> LogsList() => await (await NginxAsync()).ListLogFilesAsync();

    [Command("logs read", "The last lines of a file in /var/log/nginx (e.g. error.log or <site>.error.log).", ReadOnly = true,
        Example = "nginx logs read shop.example.com.error.log --lines 100")]
    public async Task<object> LogsRead(
        [Arg("File name in /var/log/nginx, or its full path", Positional = true, RemotePath = true)] string file,
        [Arg("How many lines from the end")] int lines = 200)
    {
        var name = file.StartsWith(DeployPaths.NginxLogs + "/", StringComparison.Ordinal) ? file[(DeployPaths.NginxLogs.Length + 1)..] : file;
        if (name.Length == 0 || name.Contains('/') || name.Contains("..") || name.Any(char.IsControl))
            throw OperationException.Invalid($"'{file}' is not a file in {DeployPaths.NginxLogs}.");

        var path = $"{DeployPaths.NginxLogs}/{name}";
        var text = await (await NginxAsync()).TailLogAsync(path, Math.Clamp(lines, 1, MaxLogLines));
        return new { path, text = string.IsNullOrWhiteSpace(text) ? "(empty)" : text };
    }

    private async Task<Outcome> ServiceActionAsync(string action)
    {
        var nginx = await NginxAsync();
        var r = await nginx.ServiceActionAsync(action);
        var state = await nginx.GetServiceStateAsync();
        if (!r.Succeeded) throw OperationException.Failed($"nginx {action} failed (state: {state}).", r.Text);
        return new Outcome($"nginx {action}: done") { Data = new { state }, Output = r.Text.Length > 0 ? r.Text : null };
    }

    /// <summary>The result of a change script: an Outcome with nginx's output, or an error saying whether anything changed.</summary>
    private static Outcome Change(CommandResult r, string ok, string fail)
    {
        var message = NginxService.Describe(r, ok, fail);
        if (!r.Succeeded) throw OperationException.Failed(message.Replace(" See the nginx output.", ""), r.Text);
        return new Outcome(message) { Output = r.Text.Length > 0 ? r.Text : null };
    }

    private static object SiteDto(NginxSite s) => new
    {
        s.Name,
        s.IsEnabled,
        s.ServerNames,
        s.ListenPorts,
        s.IsHttps,
        certificates = s.CertNames,
        s.Target,
        s.CreatedByApp,
        path = DeployPaths.SiteAvailablePath(s.Name)
    };
}
