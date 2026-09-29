using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Nginx;

public enum SiteKind { ReverseProxy, StaticFiles }

/// <summary>What the "New site" form asks for. All values are validated before a template is built.</summary>
public sealed record SiteRequest(
    string Name,
    IReadOnlyList<string> ServerNames,
    SiteKind Kind,
    string Upstream,
    string Root,
    bool WebSockets,
    bool SpaFallback,
    string MaxBodySize,
    bool ListenIpv6);

/// <summary>
/// Server-block templates for new sites. The output is a plain HTTP (port 80) site; HTTPS is
/// added later by certbot's nginx installer, which edits the same file in place.
/// </summary>
public static class SiteTemplate
{
    public const string CreatedByMarker = "Created by LinuxRemoteWorker";

    /// <summary>
    /// http{}-level map for WebSocket/SignalR upgrades, included from conf.d. The lrw_ prefix keeps it
    /// from clashing with a $connection_upgrade map the admin may already have.
    /// </summary>
    public const string WebSocketMap =
        """
        # Created by LinuxRemoteWorker: WebSocket upgrade map used by proxied sites.
        map $http_upgrade $lrw_connection_upgrade {
            default upgrade;
            ''      close;
        }
        """;

    public static string Build(SiteRequest r) => r.Kind == SiteKind.ReverseProxy ? BuildProxy(r) : BuildStatic(r);

    private static string BuildProxy(SiteRequest r)
    {
        var connection = r.WebSockets
            ? "proxy_set_header Upgrade           $http_upgrade;\n        proxy_set_header Connection        $lrw_connection_upgrade;"
            : "proxy_set_header Connection        \"\";";

        return $$"""
            # {{CreatedByMarker}} on {{DateTime.Now:yyyy-MM-dd}}: reverse proxy to {{r.Upstream}}
            # Safe to edit: every save is checked with 'nginx -t' before nginx is reloaded.
            server {
            {{Listen(r)}}
                server_name {{string.Join(' ', r.ServerNames)}};

            {{Logs(r)}}

                client_max_body_size {{r.MaxBodySize}};

                location / {
                    proxy_pass         {{r.Upstream}};
                    proxy_http_version 1.1;
                    proxy_set_header Host              $host;
                    proxy_set_header X-Real-IP         $remote_addr;
                    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
                    proxy_set_header X-Forwarded-Proto $scheme;
                    proxy_set_header X-Forwarded-Host  $host;
                    {{connection}}
                }
            }
            """;
    }

    private static string BuildStatic(SiteRequest r)
    {
        var fallback = r.SpaFallback ? "/index.html" : "=404";

        return $$"""
            # {{CreatedByMarker}} on {{DateTime.Now:yyyy-MM-dd}}: static files from {{r.Root}}
            # Safe to edit: every save is checked with 'nginx -t' before nginx is reloaded.
            server {
            {{Listen(r)}}
                server_name {{string.Join(' ', r.ServerNames)}};

                root  {{r.Root}};
                index index.html index.htm;

            {{Logs(r)}}

                location / {
                    try_files $uri $uri/ {{fallback}};
                }
            }
            """;
    }

    private static string Listen(SiteRequest r) =>
        r.ListenIpv6 ? "    listen 80;\n    listen [::]:80;" : "    listen 80;";

    private static string Logs(SiteRequest r) =>
        $"    access_log {DeployPaths.NginxLogs}/{r.Name}.access.log;\n" +
        $"    error_log  {DeployPaths.NginxLogs}/{r.Name}.error.log;";

    /// <summary>Placeholder page for a new static site whose folder has no index.html yet.</summary>
    public static string PlaceholderPage(string siteName) =>
        $"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{siteName}</title></head>
        <body><h1>{siteName}</h1><p>Served by nginx. Replace this index.html with your site.</p></body></html>
        """;
}
