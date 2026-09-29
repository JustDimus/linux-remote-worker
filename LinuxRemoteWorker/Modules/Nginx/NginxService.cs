using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>What <c>ufw</c> says about web traffic (the Allows* flags are only meaningful while ufw is active).</summary>
public sealed record WebFirewallState(bool UfwActive, bool AllowsHttp, bool AllowsHttps)
{
    public bool BlocksWeb => UfwActive && !(AllowsHttp && AllowsHttps);
}

/// <summary>
/// Server-side nginx operations. Every change follows the same transaction:
/// make the change → <c>nginx -t</c> → reload only if the test passes, otherwise restore the
/// previous files so the running configuration is never left broken.
/// </summary>
public sealed class NginxService
{
    /// <summary>Exit code of a change script whose nginx -t failed: nothing changed on the server.</summary>
    public const int ExitRolledBack = 2;

    /// <summary>Exit code when the change passed nginx -t and is in place, but the reload itself failed.</summary>
    public const int ExitReloadFailed = 3;

    private readonly SshService _ssh;

    public NginxService(SshService ssh)
    {
        _ssh = ssh;
    }

    // ── Status ─────────────────────────────────────────────────────────────

    public async Task<bool> IsInstalledAsync() =>
        !string.IsNullOrWhiteSpace(await _ssh.RunCommandAsync("command -v nginx 2>/dev/null || true"));

    public async Task<string> GetVersionAsync() =>
        (await _ssh.ExecuteAsync("nginx -v 2>&1")).Text.Replace("nginx version: ", "");

    /// <summary>active / inactive / failed … as reported by systemd.</summary>
    public async Task<string> GetServiceStateAsync()
    {
        var state = (await _ssh.ExecuteAsync("systemctl is-active nginx 2>/dev/null")).Output.Trim();
        return state.Length == 0 ? "unknown" : state;
    }

    /// <summary>
    /// Sites and the WebSocket map only load when nginx.conf includes sites-enabled/ and conf.d/
    /// (the Debian/Ubuntu default). Returns a warning when either include is missing, else null.
    /// </summary>
    public async Task<string?> CheckLayoutAsync()
    {
        var conf = Shell.Quote(DeployPaths.NginxConf);
        var r = await _ssh.ExecuteAsync(
            $"grep -Eq '^[[:space:]]*include[[:space:]].*sites-enabled' {conf} && echo sites; " +
            $"grep -Eq '^[[:space:]]*include[[:space:]].*conf\\.d' {conf} && echo confd; true");

        var missing = new List<string>();
        if (!r.Output.Contains("sites")) missing.Add($"include {DeployPaths.NginxSitesEnabled}/*;");
        if (!r.Output.Contains("confd")) missing.Add("include /etc/nginx/conf.d/*.conf;");

        return missing.Count == 0
            ? null
            : $"{DeployPaths.NginxConf} is missing '{string.Join("' and '", missing)}' inside http {{ }} - " +
              "sites managed here will not be loaded until it is added (edit nginx.conf below).";
    }

    public async Task<CommandResult> TestConfigAsync() => await _ssh.ExecuteAsync("nginx -t 2>&1");

    /// <summary>start / stop / restart / reload. Everything but stop is refused while the config test fails.</summary>
    public async Task<CommandResult> ServiceActionAsync(string action)
    {
        if (action is not ("start" or "stop" or "restart" or "reload"))
            throw new ArgumentException($"Unknown action {action}");

        return action == "stop"
            ? await _ssh.ExecuteAsync("systemctl stop nginx 2>&1")
            : await _ssh.ExecuteAsync($"nginx -t 2>&1 && systemctl {action} nginx 2>&1");
    }

    public async Task<CommandResult> InstallAsync(Action<string> onLine) =>
        await _ssh.ExecuteStreamAsync($"{Shell.AptInstall("nginx")} && systemctl enable --now nginx 2>&1", onLine);

    // ── Sites ──────────────────────────────────────────────────────────────

    public async Task<List<NginxSite>> ListSitesAsync()
    {
        // A site is enabled when any entry in sites-enabled resolves to its file — the link
        // does not have to carry the same name.
        var script = $$"""
            enabled=$(for l in {{DeployPaths.NginxSitesEnabled}}/*; do [ -e "$l" ] && readlink -f "$l"; done)
            for f in {{DeployPaths.NginxSitesAvailable}}/*; do
              [ -f "$f" ] || continue
              e=0
              printf '%s\n' "$enabled" | grep -qxF "$(readlink -f "$f")" && e=1
              printf '%s %s %s\n' '{{NginxConfigReader.SiteMarker}}' "$e" "${f##*/}"
              cat "$f"
              echo
            done
            true
            """;
        var r = await _ssh.ExecuteAsync(script);
        return NginxConfigReader.ParseListing(r.Output);
    }

    /// <summary>Reads a file exactly as stored (no trimming), for the editor.</summary>
    public async Task<string> ReadFileAsync(string path)
    {
        var r = await _ssh.ExecuteAsync($"cat {Shell.Quote(path)}");
        if (!r.Succeeded) throw new InvalidOperationException(r.Text);
        return r.Output;
    }

    /// <summary>Overwrites an existing config file; restores the old content if nginx -t fails.</summary>
    public async Task<CommandResult> SaveFileAsync(string path, string content)
    {
        var p = Shell.Quote(path);
        var script = $$"""
            exec 2>&1
            [ -f {{p}} ] || { echo {{Shell.Quote($"{path} does not exist")}}; exit 1; }
            bak=$(mktemp) || exit 1
            trap 'rm -f "$bak"' EXIT
            cp -p {{p}} "$bak" || exit 1
            {{Shell.WriteFile(path, content)}}
            if [ $? -ne 0 ]; then cp -p "$bak" {{p}}; echo {{Shell.Quote($"Could not write {path}")}}; exit 1; fi
            {{TestAndReload($"cp -p \"$bak\" {p}")}}
            """;
        return await _ssh.ExecuteAsync(script);
    }

    /// <summary>Writes the site file, links it into sites-enabled and reloads; removes both again on failure.</summary>
    public async Task<CommandResult> CreateSiteAsync(SiteRequest request)
    {
        var available = Shell.Quote(DeployPaths.SiteAvailablePath(request.Name));
        var enabled = Shell.Quote(DeployPaths.SiteEnabledPath(request.Name));

        // Extra files the site needs; a rolled-back creation removes only what this run created
        var extras = new List<string> { "created_root=0; created_index=0" };
        var rollback = $"rm -f {enabled} {available}";
        if (request.Kind == SiteKind.ReverseProxy && request.WebSockets)
        {
            var map = Shell.Quote(DeployPaths.NginxWebSocketMap);
            extras.Add($"mkdir -p {DeployPaths.NginxConfD} || exit 1");
            extras.Add($"[ -f {map} ] || " + Shell.WriteFile(DeployPaths.NginxWebSocketMap, SiteTemplate.WebSocketMap));
            extras.Add($"[ -s {map} ] || {{ echo {Shell.Quote($"Could not write {DeployPaths.NginxWebSocketMap}")}; exit 1; }}");
        }
        if (request.Kind == SiteKind.StaticFiles)
        {
            var root = Shell.Quote(request.Root);
            var index = Shell.Quote($"{request.Root}/index.html");
            extras.Add($"[ -d {root} ] || {{ mkdir -p {root} && created_root=1; }} || exit 1");
            extras.Add($"if [ ! -e {index} ]; then\n" +
                       Shell.WriteFile($"{request.Root}/index.html", SiteTemplate.PlaceholderPage(request.Name)) +
                       "\n[ $? -eq 0 ] && created_index=1\nfi");
            rollback += $"; [ $created_index = 1 ] && rm -f {index}; [ $created_root = 1 ] && rmdir {root} 2>/dev/null";
        }

        var script = $$"""
            exec 2>&1
            if [ -e {{available}} ] || [ -e {{enabled}} ] || [ -L {{enabled}} ]; then
              echo {{Shell.Quote($"A site named {request.Name} already exists.")}}
              exit 4
            fi
            mkdir -p {{DeployPaths.NginxSitesAvailable}} {{DeployPaths.NginxSitesEnabled}} || exit 1
            {{string.Join("\n", extras)}}
            {{Shell.WriteFile(DeployPaths.SiteAvailablePath(request.Name), SiteTemplate.Build(request))}}
            ln -s {{available}} {{enabled}} || { {{rollback}}; exit 1; }
            {{TestAndReload(rollback)}}
            echo {{Shell.Quote($"Site {request.Name} created and enabled.")}}
            """;
        return await _ssh.ExecuteAsync(script);
    }

    public async Task<CommandResult> EnableSiteAsync(string name)
    {
        var available = Shell.Quote(DeployPaths.SiteAvailablePath(name));
        var enabled = Shell.Quote(DeployPaths.SiteEnabledPath(name));
        var script = $$"""
            exec 2>&1
            [ -f {{available}} ] || { echo {{Shell.Quote($"No such site: {name}")}}; exit 1; }
            mkdir -p {{DeployPaths.NginxSitesEnabled}}
            ln -s {{available}} {{enabled}} || exit 1
            {{TestAndReload($"rm -f {enabled}")}}
            """;
        return await _ssh.ExecuteAsync(script);
    }

    /// <summary>Removes every sites-enabled entry that points at the site; puts them back if nginx -t fails.</summary>
    public async Task<CommandResult> DisableSiteAsync(string name)
    {
        var script = $$"""
            exec 2>&1
            {{MoveEnabledLinksAside(name)}}
            [ "$moved" -gt 0 ] || { echo {{Shell.Quote($"Site {name} was not enabled.")}}; exit 0; }
            {{TestAndReload($"mv \"$saved\"/links/* {DeployPaths.NginxSitesEnabled}/")}}
            """;
        return await _ssh.ExecuteAsync(script);
    }

    /// <summary>Deletes the site file and its links. Only an enabled site needs a test + reload.</summary>
    public async Task<CommandResult> DeleteSiteAsync(string name)
    {
        var available = Shell.Quote(DeployPaths.SiteAvailablePath(name));
        var restore = $"mv \"$saved\"/site {available}; mv \"$saved\"/links/* {DeployPaths.NginxSitesEnabled}/ 2>/dev/null";
        var script = $$"""
            exec 2>&1
            [ -f {{available}} ] || { echo {{Shell.Quote($"No such site: {name}")}}; exit 1; }
            {{MoveEnabledLinksAside(name)}}
            mv {{available}} "$saved"/site || { {{restore}}; exit 1; }
            if [ "$moved" -gt 0 ]; then
            {{TestAndReload(restore)}}
            fi
            echo {{Shell.Quote($"Site {name} deleted.")}}
            """;
        return await _ssh.ExecuteAsync(script);
    }

    /// <summary>
    /// Sets $saved (temp dir, removed on exit) and $moved (count) after moving the links that point at
    /// the site into $saved/links.
    /// </summary>
    private static string MoveEnabledLinksAside(string name) => $"""
        real=$(readlink -f {Shell.Quote(DeployPaths.SiteAvailablePath(name))})
        saved=$(mktemp -d) || exit 1
        trap 'rm -rf "$saved"' EXIT
        mkdir "$saved"/links || exit 1
        moved=0
        for l in {DeployPaths.NginxSitesEnabled}/*; do
          [ -e "$l" ] || [ -L "$l" ] || continue
          if [ "$(readlink -f "$l")" = "$real" ]; then mv "$l" "$saved"/links/ && moved=$((moved + 1)); fi
        done
        """;

    /// <summary>
    /// Keeps a change only if the whole configuration passes <c>nginx -t</c>; otherwise runs
    /// <paramref name="rollback"/> and exits <see cref="ExitRolledBack"/>. A stopped nginx stays stopped — the change
    /// simply applies on its next start.
    /// </summary>
    private static string TestAndReload(string rollback) => $"""
        if nginx -t; then
          if systemctl is-active --quiet nginx; then systemctl reload nginx || exit {ExitReloadFailed}; fi
        else
          {rollback}
          echo "nginx -t failed - the change was rolled back, nothing was reloaded."
          exit {ExitRolledBack}
        fi
        """;

    // ── Helpers used by the screen ─────────────────────────────────────────

    /// <summary>Reverse-proxy targets from the ASPNETCORE_URLS of services deployed by this app.</summary>
    public async Task<List<ServiceUpstream>> ListServiceUpstreamsAsync()
    {
        var r = await _ssh.ExecuteAsync(
            $"grep -H 'ASPNETCORE_URLS=' /etc/systemd/system/{DeployPaths.ServicePrefix}*.service 2>/dev/null; true");
        return ServiceUpstream.ParseUnitGrep(r.Output);
    }

    public async Task<bool> IsPortListeningAsync(int port)
    {
        var r = await _ssh.ExecuteAsync($"ss -Htln 2>/dev/null | awk '{{print $4}}' | grep -Eq '[:.]{port}$'");
        return r.Succeeded;
    }

    public async Task<WebFirewallState> GetFirewallStateAsync()
    {
        var status = (await _ssh.ExecuteAsync("ufw status 2>/dev/null; true")).Output;
        return ParseUfwStatus(status);
    }

    internal static WebFirewallState ParseUfwStatus(string status)
    {
        if (!status.TrimStart().StartsWith("Status: active", StringComparison.OrdinalIgnoreCase))
            return new WebFirewallState(false, false, false);

        bool http = false, https = false;
        foreach (var line in status.Split('\n'))
        {
            // "80/tcp   ALLOW   Anywhere", "80,443/tcp   ALLOW   Anywhere", "Nginx Full (v6)   ALLOW   Anywhere (v6)"
            var m = Regex.Match(line.TrimEnd('\r'), @"^(?<to>.+?)\s{2,}ALLOW(\s+IN)?\s{2,}(?<from>.+)$");
            // a rule limited to one source address does not open the site to visitors
            if (!m.Success || !m.Groups["from"].Value.StartsWith("Anywhere", StringComparison.Ordinal)) continue;

            var to = m.Groups["to"].Value.Replace("(v6)", "").Trim();
            http |= to is "Nginx Full" or "Nginx HTTP" or "WWW Full" or "WWW" || CoversTcpPort(to, 80);
            https |= to is "Nginx Full" or "Nginx HTTPS" or "WWW Full" or "WWW Secure" || CoversTcpPort(to, 443);
        }
        return new WebFirewallState(true, http, https);
    }

    // "80", "80/tcp", "80,443/tcp", "8000:9000/tcp" — udp-only rules do not count
    private static bool CoversTcpPort(string to, int port)
    {
        var slash = to.IndexOf('/');
        if (slash >= 0 && to[(slash + 1)..] != "tcp") return false;

        foreach (var part in (slash >= 0 ? to[..slash] : to).Split(','))
        {
            var range = part.Split(':');
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port) return true;
            if (range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high)
                && port >= low && port <= high) return true;
        }
        return false;
    }

    /// <summary>Opens 80 and 443 as plain port rules, which the Firewall screen lists and can remove.</summary>
    public async Task<CommandResult> AllowWebInFirewallAsync() =>
        await _ssh.ExecuteAsync("ufw allow 80/tcp 2>&1 && ufw allow 443/tcp 2>&1");

    /// <summary>
    /// True when the kernel has IPv6 addresses. Without them 'listen [::]:80' fails (module off) or is
    /// pointless (disabled by sysctl, which leaves the file empty).
    /// </summary>
    public async Task<bool> HasIpv6Async() =>
        (await _ssh.ExecuteAsync("[ -s /proc/net/if_inet6 ]")).Succeeded;

    public async Task<List<string>> ListLogFilesAsync()
    {
        return (await _ssh.ExecuteAsync($"ls -1 {DeployPaths.NginxLogs}/*.log 2>/dev/null; true")).Lines;
    }

    public async Task<string> TailLogAsync(string path, int lines) =>
        (await _ssh.ExecuteAsync($"tail -n {lines} {Shell.Quote(path)} 2>&1")).Text;
}
