using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Nginx;

public partial class NginxViewModel : BaseViewModel, IModule
{
    public string Title => "Nginx";
    public string Icon => "🌐";

    private const string ProxyKind = "Reverse proxy";
    private const string StaticKind = "Static files";
    private const int LogTailLines = 200;

    private readonly SshService _ssh;
    private readonly NginxService _nginx;
    private readonly CertbotService _certbot;
    private bool _hasIpv6 = true;

    // Overlay: IsBusyNginx covers the screen; IsStreaming adds the command's live output to it
    [ObservableProperty] private bool _isBusyNginx;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _outputLog = string.Empty;

    // nginx itself
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRunning))] private string _serviceState = string.Empty;
    public bool IsRunning => ServiceState == "active";
    [ObservableProperty] private string _layoutWarning = string.Empty;
    [ObservableProperty] private bool _firewallBlocksWeb;

    // Output of the last nginx -t / change script
    [ObservableProperty] private string _nginxOutput = string.Empty;
    [ObservableProperty] private bool _nginxOutputFailed;

    // Sites
    public ObservableCollection<NginxSite> Sites { get; } = [];

    // New site form
    public List<string> SiteKinds { get; } = [ProxyKind, StaticKind];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsProxyKind))] private string _newSiteKind = ProxyKind;
    public bool IsProxyKind => NewSiteKind == ProxyKind;
    [ObservableProperty] private string _newSiteDomains = string.Empty;
    [ObservableProperty] private string _newSiteName = string.Empty;
    [ObservableProperty] private string _newSiteUpstream = "http://127.0.0.1:5000";
    [ObservableProperty] private string _newSiteRoot = string.Empty;
    [ObservableProperty] private bool _newSiteWebSockets = true;
    [ObservableProperty] private bool _newSiteSpa;
    [ObservableProperty] private string _newSiteMaxBody = "10m";
    public ObservableCollection<ServiceUpstream> ServiceUpstreams { get; } = [];
    [ObservableProperty] private ServiceUpstream? _selectedServiceUpstream;

    // Config editor (a site file or nginx.conf)
    [ObservableProperty] private string? _editorPath;
    [ObservableProperty] private string _editorContent = string.Empty;
    private NginxSite? _editorSite;
    private string? _editorHost;

    // Certificates
    [ObservableProperty] private bool _certbotReady;
    [ObservableProperty] private string _certbotSummary = string.Empty;
    [ObservableProperty] private bool _certbotInstalled;
    [ObservableProperty] private bool _autoRenewActive;
    [ObservableProperty] private string _autoRenewText = string.Empty;
    public ObservableCollection<CertificateInfo> Certificates { get; } = [];
    [ObservableProperty] private string _certDomains = string.Empty;
    [ObservableProperty] private string _certName = string.Empty;
    [ObservableProperty] private string _certEmail = string.Empty;
    [ObservableProperty] private bool _certRedirect = true;
    [ObservableProperty] private bool _certDryRun;

    // nginx log files
    public ObservableCollection<string> LogFiles { get; } = [];
    [ObservableProperty] private string? _selectedLogFile;
    [ObservableProperty] private string _logOutput = string.Empty;

    public NginxViewModel(SshService ssh)
    {
        _ssh = ssh;
        _nginx = new NginxService(ssh);
        _certbot = new CertbotService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    // ── Loading ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync() => await WorkAsync(async () =>
    {
        await LoadAllAsync();
        SetStatus(string.Empty);
    }, "Reading nginx state...");

    private async Task LoadAllAsync()
    {
        // A file opened on another server must never be saved onto this one
        if (EditorPath != null && _editorHost != _ssh.Host)
            CloseEditor();

        IsInstalled = await _nginx.IsInstalledAsync();
        if (!IsInstalled) return;

        Version = await _nginx.GetVersionAsync();
        ServiceState = await _nginx.GetServiceStateAsync();
        LayoutWarning = await _nginx.CheckLayoutAsync() ?? string.Empty;
        FirewallBlocksWeb = (await _nginx.GetFirewallStateAsync()).BlocksWeb;
        _hasIpv6 = await _nginx.HasIpv6Async();

        await LoadSitesAsync();
        await LoadCertificatesAsync();
        await LoadUpstreamsAsync();
        await LoadLogFilesAsync();
    }

    private async Task LoadSitesAsync()
    {
        var sites = await _nginx.ListSitesAsync();
        Sites.Clear();
        foreach (var s in sites) Sites.Add(s);
        LinkCertificatesToSites();
    }

    private async Task LoadCertificatesAsync()
    {
        var status = await _certbot.GetStatusAsync();
        CertbotInstalled = status.IsInstalled;
        CertbotReady = status.IsInstalled && status.HasNginxPlugin;
        CertbotSummary = !status.IsInstalled ? "certbot is not installed"
            : !status.HasNginxPlugin ? $"{status.Version} - the nginx plugin (python3-certbot-nginx) is missing"
            : status.Version;
        AutoRenewActive = status.AutoRenewActive;
        AutoRenewText = status.AutoRenewText;

        Certificates.Clear();
        if (status.IsInstalled)
            foreach (var c in await _certbot.ListCertificatesAsync()) Certificates.Add(c);
        LinkCertificatesToSites();
    }

    // Which sites reference which certificate is only known once both lists are loaded
    private void LinkCertificatesToSites()
    {
        for (var i = 0; i < Certificates.Count; i++)
        {
            var cert = Certificates[i];
            var usedBy = Sites.Where(s => s.CertNames.Contains(cert.Name)).Select(s => s.Name).ToList();
            if (!usedBy.SequenceEqual(cert.UsedBy))
                Certificates[i] = cert with { UsedBy = usedBy };
        }
    }

    private async Task LoadUpstreamsAsync()
    {
        var upstreams = await _nginx.ListServiceUpstreamsAsync();
        ServiceUpstreams.Clear();
        foreach (var u in upstreams) ServiceUpstreams.Add(u);
    }

    private async Task LoadLogFilesAsync()
    {
        var files = await _nginx.ListLogFilesAsync();
        var keep = SelectedLogFile;
        LogFiles.Clear();
        foreach (var f in files) LogFiles.Add(f);
        SelectedLogFile = keep != null && files.Contains(keep)
            ? keep
            : files.FirstOrDefault(f => f.EndsWith("/error.log")) ?? files.FirstOrDefault();
    }

    // ── nginx service ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallAsync() => await StreamAsync(async append =>
    {
        append("→ Installing nginx...");
        var r = await _nginx.InstallAsync(append);
        if (!r.Succeeded)
            throw new InvalidOperationException($"Installing nginx failed (exit {r.ExitCode}) - see the output.");
        append("\n✓ nginx installed and started");
        await LoadAllAsync();
        SetStatus("nginx installed");
    }, "Installing nginx...");

    [RelayCommand]
    private async Task TestConfigAsync() => await WorkAsync(async () =>
    {
        var r = await _nginx.TestConfigAsync();
        ShowResult(r, "Configuration test passed", "Configuration test failed");
    }, "Running nginx -t...");

    [RelayCommand]
    private async Task ReloadNginxAsync() => await ServiceActionAsync("reload");

    [RelayCommand]
    private async Task RestartNginxAsync() => await ServiceActionAsync("restart");

    [RelayCommand]
    private async Task StartNginxAsync() => await ServiceActionAsync("start");

    [RelayCommand]
    private async Task StopNginxAsync()
    {
        if (!Confirm("Stop nginx?\n\nEvery site on this server goes offline until nginx is started again.", "Stop nginx"))
            return;
        await ServiceActionAsync("stop");
    }

    private async Task ServiceActionAsync(string action) => await WorkAsync(async () =>
    {
        var r = await _nginx.ServiceActionAsync(action);
        ServiceState = await _nginx.GetServiceStateAsync();
        ShowResult(r, $"nginx {action}: done", $"nginx {action} failed");
    }, $"nginx {action}...");

    [RelayCommand]
    private async Task AllowWebTrafficAsync() => await WorkAsync(async () =>
    {
        var r = await _nginx.AllowWebInFirewallAsync();
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
        FirewallBlocksWeb = (await _nginx.GetFirewallStateAsync()).BlocksWeb;
        SetStatus("Firewall: ports 80 and 443 are open");
    }, "Opening ports 80 and 443...");

    [RelayCommand]
    private void ClearNginxOutput() => NginxOutput = string.Empty;

    // ── Sites ──────────────────────────────────────────────────────────────

    partial void OnSelectedServiceUpstreamChanged(ServiceUpstream? value)
    {
        if (value != null) NewSiteUpstream = value.Url;
    }

    [RelayCommand]
    private async Task CreateSiteAsync()
    {
        var (names, error) = NginxInput.ParseServerNames(NewSiteDomains);
        var name = string.IsNullOrWhiteSpace(NewSiteName)
            ? names.FirstOrDefault(n => n != "_" && !n.StartsWith('*')) ?? string.Empty
            : NewSiteName.Trim();
        var kind = IsProxyKind ? SiteKind.ReverseProxy : SiteKind.StaticFiles;
        var upstream = NewSiteUpstream.Trim();
        var root = string.IsNullOrWhiteSpace(NewSiteRoot) ? DeployPaths.SiteWebRoot(name) : NewSiteRoot.Trim();
        var maxBody = NewSiteMaxBody.Trim();

        error ??= NginxInput.ValidateSiteName(name)
                  ?? (kind == SiteKind.ReverseProxy ? NginxInput.ValidateUpstream(upstream) : NginxInput.ValidateRoot(root))
                  ?? NginxInput.ValidateBodySize(maxBody);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        var request = new SiteRequest(name, names, kind, upstream, root, NewSiteWebSockets, NewSiteSpa, maxBody, _hasIpv6);

        await WorkAsync(async () =>
        {
            var r = await _nginx.CreateSiteAsync(request);
            await LoadSitesAsync();
            await LoadLogFilesAsync();
            if (!ShowResult(r, $"Site {name} created and enabled", $"Site {name} was not created"))
                return;

            NewSiteDomains = string.Empty;
            NewSiteName = string.Empty;
            NewSiteRoot = string.Empty;
            SelectedServiceUpstream = null;

            // The most common first-day problem: the proxy works, the app behind it is not running
            if (kind == SiteKind.ReverseProxy && IsLoopback(upstream)
                && !await _nginx.IsPortListeningAsync(NginxInput.UpstreamPort(upstream)))
            {
                SetStatus($"Site {name} created, but nothing listens on port {NginxInput.UpstreamPort(upstream)} yet - " +
                          "nginx answers 502 Bad Gateway until the service is running.", isError: true);
            }
        }, $"Creating site {name}...");
    }

    private static bool IsLoopback(string url) =>
        url.Contains("://127.0.0.1") || url.Contains("://localhost") || url.Contains("://[::1]");

    [RelayCommand]
    private async Task EnableSiteAsync(NginxSite site) => await WorkAsync(async () =>
    {
        var r = await _nginx.EnableSiteAsync(site.Name);
        await LoadSitesAsync();
        ShowResult(r, $"Site {site.Name} enabled", $"Site {site.Name} was not enabled");
    }, $"Enabling {site.Name}...");

    [RelayCommand]
    private async Task DisableSiteAsync(NginxSite site) => await WorkAsync(async () =>
    {
        var r = await _nginx.DisableSiteAsync(site.Name);
        await LoadSitesAsync();
        ShowResult(r, $"Site {site.Name} disabled - its file stays in sites-available", $"Site {site.Name} was not disabled");
    }, $"Disabling {site.Name}...");

    [RelayCommand]
    private async Task DeleteSiteAsync(NginxSite site)
    {
        if (!Confirm($"Delete site '{site.Name}'?\n\n{DeployPaths.SiteAvailablePath(site.Name)} and its link in " +
                     "sites-enabled are removed and nginx is reloaded. Web content and log files are kept.", "Delete site"))
            return;

        await WorkAsync(async () =>
        {
            var r = await _nginx.DeleteSiteAsync(site.Name);
            await LoadSitesAsync();
            if (!ShowResult(r, $"Site {site.Name} deleted", $"Site {site.Name} was not deleted"))
                return;

            if (EditorPath == DeployPaths.SiteAvailablePath(site.Name))
                CloseEditor();

            // A certificate nobody uses any more would otherwise keep renewing forever
            foreach (var cert in Certificates.Where(c => site.CertNames.Contains(c.Name) && c.UsedBy.Count == 0).ToList())
            {
                if (Confirm($"Certificate '{cert.Name}' ({cert.DomainsText}) is no longer used by any site.\n\nDelete it too?",
                        "Delete certificate"))
                    await DeleteCertificateCoreAsync(cert);
            }
        }, $"Deleting {site.Name}...");
    }

    // ── Editor ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task EditSiteAsync(NginxSite site) =>
        await OpenEditorAsync(DeployPaths.SiteAvailablePath(site.Name), site);

    [RelayCommand]
    private async Task EditMainConfigAsync() => await OpenEditorAsync(DeployPaths.NginxConf, null);

    private async Task OpenEditorAsync(string path, NginxSite? site) => await WorkAsync(async () =>
    {
        EditorContent = await _nginx.ReadFileAsync(path);
        EditorPath = path;
        _editorSite = site;
        _editorHost = _ssh.Host;
        SetStatus($"Editing {path}");
    }, $"Opening {path}...");

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (EditorPath is not { } path) return;

        await WorkAsync(async () =>
        {
            var r = await _nginx.SaveFileAsync(path, EditorContent);
            await LoadSitesAsync();
            // the site may have been enabled or disabled since the editor was opened
            var site = _editorSite == null ? null : Sites.FirstOrDefault(s => s.Name == _editorSite.Name);
            var ok = site is { IsEnabled: false }
                ? $"Saved {path}. The site is disabled, so nginx -t checks it only once you enable it."
                : $"Saved {path}; nginx -t passed and nginx was reloaded";
            ShowResult(r, ok, $"{path} was not saved");
        }, "Saving, testing and reloading...");
    }

    [RelayCommand]
    private async Task RevertEditorAsync()
    {
        if (EditorPath is { } path)
            await OpenEditorAsync(path, _editorSite);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        EditorPath = null;
        EditorContent = string.Empty;
        _editorSite = null;
        _editorHost = null;
    }

    // ── Certificates ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallCertbotAsync() => await StreamAsync(async append =>
    {
        append("→ Installing certbot and its nginx plugin...");
        var r = await _certbot.InstallAsync(append);
        if (!r.Succeeded)
            throw new InvalidOperationException($"Installing certbot failed (exit {r.ExitCode}) - see the output.");
        append("\n✓ certbot installed");
        await LoadCertificatesAsync();
        SetStatus("certbot installed");
    }, "Installing certbot...");

    [RelayCommand]
    private async Task EnableAutoRenewAsync() => await WorkAsync(async () =>
    {
        var r = await _certbot.EnableAutoRenewAsync();
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
        await LoadCertificatesAsync();
        SetStatus("Automatic renewal enabled");
    }, "Enabling automatic renewal...");

    /// <summary>Fills the certificate form from a site, then offers to request the certificate right away.</summary>
    [RelayCommand]
    private async Task SecureSiteAsync(NginxSite site)
    {
        if (!CertbotReady)
        {
            SetStatus("Install certbot with its nginx plugin first (SSL CERTIFICATES section).", isError: true);
            return;
        }
        if (!site.IsEnabled)
        {
            SetStatus($"Enable site {site.Name} first - certbot installs the certificate into an enabled site.", isError: true);
            return;
        }
        if (site.CertificateDomains.Count == 0)
        {
            SetStatus($"Site {site.Name} has no public domain name in server_name - a certificate needs one.", isError: true);
            return;
        }

        CertDomains = string.Join(' ', site.CertificateDomains);
        CertName = site.Name;

        var what = CertDryRun
            ? "Test (dry run) a Let's Encrypt certificate request for:\n\n{0}\n\nNothing on the server changes. " +
              "Untick 'Dry run' in the SSL CERTIFICATES section for the real certificate."
            : "Request a Let's Encrypt certificate for:\n\n{0}\n\ncertbot adds HTTPS to site '" + site.Name + "'" +
              (CertRedirect ? " and redirects HTTP to HTTPS." : ".");
        if (Confirm(string.Format(what, CertDomains) +
                    "\n\nChoose No to adjust the request in the SSL CERTIFICATES section first.", "Enable HTTPS"))
            await IssueCertificateAsync();
        else
            SetStatus("Certificate form filled in - review it in the SSL CERTIFICATES section.");
    }

    [RelayCommand]
    private async Task IssueCertificateAsync()
    {
        if (!CertbotReady)
        {
            SetStatus("Install certbot with its nginx plugin first (SSL CERTIFICATES section).", isError: true);
            return;
        }

        var (domains, error) = NginxInput.ParseCertificateDomains(CertDomains);
        var certName = string.IsNullOrWhiteSpace(CertName) ? domains.FirstOrDefault() ?? string.Empty : CertName.Trim();
        var email = CertEmail.Trim();
        var dryRun = CertDryRun;

        error ??= NginxInput.ValidateCertName(certName);
        if (error == null && email.Length > 0)
            error = NginxInput.ValidateEmail(email);

        // certbot's installer puts the certificate into the enabled site whose server_name matches
        if (error == null && !dryRun)
        {
            var unserved = domains.Where(d => !Sites.Any(s => s.IsEnabled && Serves(s, d))).ToList();
            if (unserved.Count > 0)
                error = $"No enabled site has server_name {string.Join(", ", unserved)}. " +
                        "Create or enable that site first, so certbot can install the certificate into it.";
        }

        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        await StreamAsync(async append =>
        {
            if (!await PreflightDnsAsync(domains, append))
                throw new InvalidOperationException("Stopped before contacting Let's Encrypt: fix DNS first (see the output).");

            append(dryRun
                ? "\n→ Dry run against the Let's Encrypt staging server - nothing on this server changes..."
                : "\n→ Requesting the certificate from Let's Encrypt...");
            var r = await _certbot.IssueAsync(domains, certName, email, CertRedirect, dryRun, append);
            if (!r.Succeeded)
                throw new InvalidOperationException("certbot failed - the reason is in the output.");

            await LoadSitesAsync();
            await LoadCertificatesAsync();
            SetStatus(dryRun
                ? "Dry run passed - the real request should succeed"
                : $"HTTPS is on for {string.Join(", ", domains)}");
        }, dryRun ? "Testing the certificate request..." : "Requesting the certificate...");
    }

    private static bool Serves(NginxSite site, string domain) =>
        site.ServerNames.Any(n =>
            n.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            (n.StartsWith("*.") && domain.EndsWith(n[1..], StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Let's Encrypt validates over plain HTTP, so every name must resolve to this server. A name that
    /// does not resolve at all is certain to fail (and would count against the failed-validation rate
    /// limit), so it stops the request. An address mismatch only warns: behind NAT or a cloud public IP
    /// the public address never appears on the server's own interfaces.
    /// </summary>
    private async Task<bool> PreflightDnsAsync(IReadOnlyList<string> domains, Action<string> append)
    {
        append("→ Checking DNS...");
        var local = await _certbot.ServerAddressesAsync();
        var ok = true;
        foreach (var domain in domains)
        {
            var addresses = await _certbot.ResolveAsync(domain);
            if (addresses.Count == 0)
            {
                append($"✗ {domain} does not resolve - add an A/AAAA record that points to this server.");
                ok = false;
            }
            else if (local.Count > 0 && !addresses.Intersect(local).Any())
            {
                append($"⚠ {domain} → {string.Join(", ", addresses)}, but this server has {string.Join(", ", local)}. " +
                       "Fine behind NAT or a cloud public IP; otherwise the validation will fail.");
            }
            else
            {
                append($"✓ {domain} → {string.Join(", ", addresses)}");
            }
        }
        return ok;
    }

    [RelayCommand]
    private async Task RenewCertificateAsync(CertificateInfo cert)
    {
        if (!Confirm($"Renew '{cert.Name}' now, even though it may not be due?\n\n" +
                     "certbot already renews automatically about 30 days before expiry. Let's Encrypt allows " +
                     "only 5 certificates for the same set of names per week.", "Renew certificate"))
            return;

        await StreamAsync(async append =>
        {
            var r = await _certbot.ForceRenewAsync(cert.Name, append);
            if (!r.Succeeded)
                throw new InvalidOperationException($"Renewing {cert.Name} failed - see the output.");
            await LoadCertificatesAsync();
            SetStatus($"Certificate {cert.Name} renewed and nginx reloaded");
        }, $"Renewing {cert.Name}...");
    }

    [RelayCommand]
    private async Task RenewDueAsync() => await StreamAsync(async append =>
    {
        var r = await _certbot.RenewDueAsync(append);
        if (!r.Succeeded)
            throw new InvalidOperationException("Renewal failed - see the output.");
        await LoadCertificatesAsync();
        SetStatus("Renewal run finished - certificates that were due are renewed");
    }, "Renewing certificates that are due...");

    [RelayCommand]
    private async Task TestRenewalAsync() => await StreamAsync(async append =>
    {
        var r = await _certbot.TestRenewalAsync(append);
        if (!r.Succeeded)
            throw new InvalidOperationException("The renewal dry run failed, so automatic renewal would fail too - see the output.");
        SetStatus("Renewal dry run passed - automatic renewal will work");
    }, "Simulating renewal...");

    [RelayCommand]
    private async Task DeleteCertificateAsync(CertificateInfo cert)
    {
        if (!Confirm($"Delete certificate '{cert.Name}' ({cert.DomainsText})?\n\n" +
                     "Its files and renewal settings are removed from the server. This cannot be undone.", "Delete certificate"))
            return;

        await WorkAsync(async () => await DeleteCertificateCoreAsync(cert), $"Deleting {cert.Name}...");
    }

    private async Task DeleteCertificateCoreAsync(CertificateInfo cert)
    {
        // nginx refuses to reload (and to start after a reboot) when a certificate file is missing
        var users = await _certbot.FindConfigsUsingAsync(cert.Name);
        if (users.Count > 0)
        {
            SetStatus($"Certificate {cert.Name} is still used by {string.Join(", ", users)}. " +
                      "Delete those sites or remove their ssl_certificate lines first.", isError: true);
            return;
        }

        var r = await _certbot.DeleteAsync(cert.Name);
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
        await LoadCertificatesAsync();
        SetStatus($"Certificate {cert.Name} deleted");
    }

    // ── Logs ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowLogAsync()
    {
        if (SelectedLogFile is not { } file) return;

        await WorkAsync(async () =>
        {
            var text = await _nginx.TailLogAsync(file, LogTailLines);
            LogOutput = string.IsNullOrWhiteSpace(text) ? "(empty)" : text;
            SetStatus($"Last {LogTailLines} lines of {file}");
        }, $"Reading {file}...");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task WorkAsync(Func<Task> action, string busyMessage)
    {
        IsBusyNginx = true;
        await RunSafeAsync(action, busyMessage);
        IsBusyNginx = false;
    }

    // Like WorkAsync, but shows the command's live output in the overlay and keeps it afterwards
    private async Task StreamAsync(Func<Action<string>, Task> action, string busyMessage)
    {
        OutputLog = string.Empty;
        IsStreaming = true;
        await WorkAsync(() => action(line => OutputLog += line + "\n"), busyMessage);
        IsStreaming = false;
    }

    /// <summary>Shows a change script's output under the header and sets the status line. True on success.</summary>
    private bool ShowResult(CommandResult r, string okMessage, string failMessage)
    {
        NginxOutput = r.Text;
        NginxOutputFailed = !r.Succeeded;

        var message = r.ExitCode switch
        {
            0 => okMessage,
            NginxService.ExitRolledBack => $"{failMessage}: nginx -t failed, so nothing was changed. See the nginx output.",
            NginxService.ExitReloadFailed => "The change is in place and passes nginx -t, but the reload failed. See the nginx output.",
            _ => $"{failMessage}. See the nginx output."
        };
        SetStatus(message, isError: !r.Succeeded);
        return r.Succeeded;
    }
}
