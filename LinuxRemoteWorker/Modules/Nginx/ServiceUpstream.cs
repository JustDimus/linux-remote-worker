using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>A .NET service deployed by this app, offered as a reverse-proxy target.</summary>
public record ServiceUpstream(string AppName, string Url)
{
    public string Display => $"{AppName}  →  {Url}";

    private static readonly Regex UnitLine = new(
        $@"/{Regex.Escape(DeployPaths.ServicePrefix)}(?<app>[^/]+)\.service:.*ASPNETCORE_URLS=(?<urls>[^""\s]+)");

    private static readonly Regex UrlParts = new(
        @"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/]+)(:(?<port>\d+))?", RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <c>grep -H ASPNETCORE_URLS lrw-*.service</c> output into proxy targets.
    /// </summary>
    public static List<ServiceUpstream> ParseUnitGrep(string grepOutput)
    {
        var result = new List<ServiceUpstream>();
        foreach (var line in grepOutput.Split('\n'))
        {
            var m = UnitLine.Match(line);
            if (!m.Success) continue;
            var url = ToLocalUrl(m.Groups["urls"].Value);
            if (url != null)
                result.Add(new ServiceUpstream(m.Groups["app"].Value, url));
        }
        return result;
    }

    /// <summary>
    /// Turns an ASPNETCORE_URLS value (e.g. "http://0.0.0.0:5000;https://*:5001") into the address
    /// nginx on the same machine should proxy to. Prefers plain http; wildcard hosts become loopback.
    /// </summary>
    public static string? ToLocalUrl(string aspNetCoreUrls)
    {
        var urls = aspNetCoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                     ?? urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (chosen == null) return null;

        var m = UrlParts.Match(chosen);
        if (!m.Success) return null;

        var scheme = m.Groups["scheme"].Value.ToLowerInvariant();
        var host = m.Groups["host"].Value;
        if (host is "0.0.0.0" or "*" or "+" or "[::]" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            host = "127.0.0.1";
        var port = m.Groups["port"].Success ? m.Groups["port"].Value : (scheme == "https" ? "443" : "80");

        return $"{scheme}://{host}:{port}";
    }
}
