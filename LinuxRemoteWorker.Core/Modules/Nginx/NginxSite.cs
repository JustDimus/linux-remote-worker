namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>A server-block file in sites-available, as read from the server.</summary>
public record NginxSite(
    string Name,
    bool IsEnabled,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ListenPorts,
    bool IsHttps,
    IReadOnlyList<string> CertNames,
    string Target,
    bool CreatedByApp)
{
    public string ServerNamesText => ServerNames.Count == 0 ? "(no server_name)" : string.Join("  ", ServerNames);

    public string DetailsText => string.Join("  ·  ", new[]
    {
        ListenPorts.Count == 0 ? "" : "ports " + string.Join(", ", ListenPorts),
        Target
    }.Where(s => s.Length > 0));

    /// <summary>Names that can go on a public certificate (drops _, localhost, wildcards, regexes and IPs).</summary>
    public IReadOnlyList<string> CertificateDomains => ServerNames.Where(NginxInput.IsPublicHostname).ToList();

    /// <summary>True when a server_name of this site matches <paramref name="domain"/> exactly or by *.wildcard.</summary>
    public bool Serves(string domain) =>
        ServerNames.Any(n =>
            n.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            (n.StartsWith("*.") && domain.EndsWith(n[1..], StringComparison.OrdinalIgnoreCase)));
}
