using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>
/// Validation for everything typed into the nginx screen. Values end up in root-owned config
/// files and shell commands, so each one is checked against a strict whitelist.
/// Every method returns an error message, or null when the value is acceptable.
/// </summary>
public static class NginxInput
{
    private static readonly Regex SiteName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

    private static readonly Regex Hostname = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$");

    private static readonly Regex Ipv4 = new(
        @"^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$");

    private static readonly Regex Upstream = new(
        @"^https?://(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:(?<port>\d{1,5}))?(/[A-Za-z0-9._~/-]*)?$");

    private static readonly Regex RootPath = new(@"^/[A-Za-z0-9._/-]+$");

    // Static roots stay in the usual web-content areas: anything else (/, /etc, a home directory
    // with .ssh, the deploy-key folder) would publish files that must stay private.
    private static readonly string[] RootAreas = ["/var/www/", "/srv/", "/opt/"];
    private static readonly Regex BodySize = new(@"^\d{1,6}[kKmMgG]?$");
    private static readonly Regex Email = new(@"^[^@\s'""\\;]+@[^@\s'""\\;]+\.[^@\s'""\\;]+$");

    public static string? ValidateSiteName(string name) => ValidateName(name, "Site name");

    /// <summary>
    /// For names read back from the server (a site file someone else created may break the strict
    /// rule): only what would escape sites-available is rejected.
    /// </summary>
    public static string? ValidateExistingName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0 && !name.Any(char.IsControl)
            ? null
            : $"'{name}' is not a file name inside {DeployPaths.NginxSitesAvailable}.";

    public static string? ValidateCertName(string name) => ValidateName(name, "Certificate name");

    // Site and certificate names become file/directory names on the server
    private static string? ValidateName(string name, string what) =>
        SiteName.IsMatch(name) && !name.Contains("..")
            ? null
            : $"{what}: letters, digits, '.', '-' and '_' only (for example example.com).";

    /// <summary>
    /// Splits a space/comma separated list into lower-case server names.
    /// Accepts host names, *.wildcards, IPv4 addresses, localhost and the catch-all '_'.
    /// </summary>
    public static (List<string> Names, string? Error) ParseServerNames(string input)
    {
        var names = input.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (names.Count == 0)
            return (names, "Enter at least one domain (or the server's IP address).");

        var bad = names.FirstOrDefault(n => !IsServerName(n));
        return bad == null ? (names, null) : (names, $"'{bad}' is not a valid domain name.");
    }

    /// <summary>Names for a Let's Encrypt HTTP-01 certificate: public host names only.</summary>
    public static (List<string> Names, string? Error) ParseCertificateDomains(string input)
    {
        var (names, error) = ParseServerNames(input);
        if (error != null) return (names, error);

        var bad = names.FirstOrDefault(n => !IsPublicHostname(n));
        if (bad == null) return (names, null);

        return (names, bad.StartsWith("*.")
            ? $"'{bad}': wildcard certificates need DNS validation, which this screen does not do."
            : $"'{bad}' cannot get a public certificate - use a real domain name that points to this server.");
    }

    public static bool IsPublicHostname(string name) =>
        Hostname.IsMatch(name) && !name.EndsWith(".local", StringComparison.Ordinal);

    private static bool IsServerName(string name) =>
        name is "_" or "localhost" ||
        Hostname.IsMatch(name) ||
        (name.StartsWith("*.") && Hostname.IsMatch(name[2..])) ||
        Ipv4.IsMatch(name);

    public static string? ValidateUpstream(string url)
    {
        var m = Upstream.Match(url);
        if (!m.Success)
            return "Upstream must look like http://127.0.0.1:5000";
        if (m.Groups["port"].Success && int.Parse(m.Groups["port"].Value) is < 1 or > 65535)
            return "Upstream port must be between 1 and 65535.";
        return null;
    }

    public static string? ValidateRoot(string path)
    {
        var dir = path.TrimEnd('/');
        if (!RootPath.IsMatch(path) || dir.Split('/').Skip(1).Any(s => s is "" or "." or ".."))
            return "Root folder must be a plain absolute path such as /var/www/example.com";

        if (!RootAreas.Any(area => dir.StartsWith(area, StringComparison.Ordinal) && dir.Length > area.Length))
            return "Root folder must be inside /var/www, /srv or /opt (for example /var/www/example.com).";

        if ((dir + "/").StartsWith(DeployPaths.Base + "/", StringComparison.Ordinal)
            && !dir.StartsWith(DeployPaths.Apps + "/", StringComparison.Ordinal))
            return $"Inside {DeployPaths.Base} only published apps ({DeployPaths.Apps}/...) can be served - keys and logs stay private.";

        return null;
    }

    public static string? ValidateBodySize(string size) =>
        BodySize.IsMatch(size) ? null : "Max upload size: a number with an optional k, m or g suffix (for example 10m).";

    public static string? ValidateEmail(string email) =>
        Email.IsMatch(email) ? null : "That e-mail address does not look valid.";

    /// <summary>
    /// Turns the "New site" form into a validated request. The site name defaults to the first concrete
    /// domain, a static root to /var/www/&lt;name&gt;.
    /// </summary>
    public static (SiteRequest? Request, string? Error) BuildSiteRequest(
        string domains, string? name, SiteKind kind, string? upstream, string? root,
        bool webSockets, bool spaFallback, string maxBodySize, bool listenIpv6)
    {
        var (names, error) = ParseServerNames(domains);
        var siteName = string.IsNullOrWhiteSpace(name)
            ? names.FirstOrDefault(n => n != "_" && !n.StartsWith('*')) ?? string.Empty
            : name.Trim();
        var target = upstream?.Trim() ?? string.Empty;
        var dir = string.IsNullOrWhiteSpace(root) ? DeployPaths.SiteWebRoot(siteName) : root.Trim();
        var maxBody = maxBodySize.Trim();

        error ??= ValidateSiteName(siteName)
                  ?? (kind == SiteKind.ReverseProxy ? ValidateUpstream(target) : ValidateRoot(dir))
                  ?? ValidateBodySize(maxBody);
        return error != null
            ? (null, error)
            : (new SiteRequest(siteName, names, kind, target, dir, webSockets, spaFallback, maxBody, listenIpv6), null);
    }

    /// <summary>Validates a certificate request. The certificate name defaults to the first domain.</summary>
    public static (CertificateRequest? Request, string? Error) ParseCertificateRequest(string domains, string? certName, string? email)
    {
        var (names, error) = ParseCertificateDomains(domains);
        var name = string.IsNullOrWhiteSpace(certName) ? names.FirstOrDefault() ?? string.Empty : certName.Trim();
        var mail = email?.Trim() ?? string.Empty;

        error ??= ValidateCertName(name);
        if (error == null && mail.Length > 0)
            error = ValidateEmail(mail);
        return error != null
            ? (null, error)
            : (new CertificateRequest(names, name, mail.Length == 0 ? null : mail), null);
    }

    /// <summary>The port in an upstream URL (explicit, or the scheme default).</summary>
    public static int UpstreamPort(string url)
    {
        var m = Upstream.Match(url);
        if (m.Success && m.Groups["port"].Success) return int.Parse(m.Groups["port"].Value);
        return url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }
}
