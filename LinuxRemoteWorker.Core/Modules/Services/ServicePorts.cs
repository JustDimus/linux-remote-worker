using System.Globalization;
using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Services;

/// <summary>
/// Ports in ASPNETCORE_URLS, which deployed service uses which port, and which ports something listens on -
/// so a second instance of an app cannot be deployed onto a port that is already taken.
/// </summary>
public static class ServicePorts
{
    private static readonly Regex UrlPort = new(@"^(?<scheme>https?)://(\[[^\]]*\]|[^:/;]+)(:(?<port>\d{1,5}))?", RegexOptions.IgnoreCase);

    private static readonly Regex UnitUrls = new(
        $@"/{Regex.Escape(DeployPaths.ServicePrefix)}(?<app>[^/]+)\.service:\s*Environment=""?ASPNETCORE_URLS=(?<urls>[^""\s]+)");

    /// <summary>"http://0.0.0.0:5000;https://*:5001" → 5000, 5001. A URL without a port means 80 or 443.</summary>
    public static List<int> Parse(string aspNetCoreUrls)
    {
        var ports = new List<int>();
        foreach (var url in aspNetCoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = UrlPort.Match(url);
            if (!m.Success) continue;
            var port = m.Groups["port"].Success
                ? int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture)
                : m.Groups["scheme"].Value.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
            if (!ports.Contains(port)) ports.Add(port);
        }
        return ports;
    }

    /// <summary>Ports per deployed app, from `grep -H ASPNETCORE_URLS= /etc/systemd/system/lrw-*.service`.</summary>
    public static Dictionary<string, List<int>> FromUnits(string grepOutput)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var line in grepOutput.Split('\n'))
        {
            var m = UnitUrls.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            var app = m.Groups["app"].Value;
            if (!result.TryGetValue(app, out var ports)) result[app] = ports = [];
            ports.AddRange(Parse(m.Groups["urls"].Value).Where(p => !ports.Contains(p)));
        }
        return result;
    }

    /// <summary>Listening ports from the local-address column of `ss -Htln`: 0.0.0.0:5000, [::]:22, *:80, 127.0.0.1:5432.</summary>
    public static HashSet<int> Listening(string localAddresses)
    {
        var ports = new HashSet<int>();
        foreach (var raw in localAddresses.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(raw[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                ports.Add(port);
        }
        return ports;
    }

    /// <summary>
    /// Why <paramref name="app"/> cannot use <paramref name="ports"/>: another deployed service is configured for
    /// one of them, or something else listens on it. The app's own current ports are fine - redeploying it
    /// keeps them. Empty when the ports are free.
    /// </summary>
    public static List<string> Conflicts(string app, IEnumerable<int> ports, Dictionary<string, List<int>> units, HashSet<int> listening)
    {
        var own = units.TryGetValue(app, out var mine) ? mine : [];
        var problems = new List<string>();
        foreach (var port in ports)
        {
            var other = units.FirstOrDefault(u => u.Key != app && u.Value.Contains(port)).Key;
            if (other != null)
                problems.Add($"port {port} is already used by service {other}");
            else if (listening.Contains(port) && !own.Contains(port))
                problems.Add($"port {port} is already in use on the server (see `ss -tlnp`)");
        }
        return problems;
    }

    /// <summary>The first port from 5000 up that no deployed service is configured for and nothing listens on.</summary>
    public static int SuggestFree(Dictionary<string, List<int>> units, HashSet<int> listening)
    {
        var taken = units.Values.SelectMany(p => p).Concat(listening).ToHashSet();
        var port = 5000;
        while (taken.Contains(port)) port++;
        return port;
    }
}
