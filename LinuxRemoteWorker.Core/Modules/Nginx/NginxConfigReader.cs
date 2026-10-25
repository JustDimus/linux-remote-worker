using System.Text;
using System.Text.RegularExpressions;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>
/// Reads the facts the UI shows (server names, ports, TLS, target) out of nginx config text.
/// It tokenises like nginx does — comments, quotes, ';', '{' and '}' — but does not validate;
/// validation is always left to 'nginx -t' on the server.
/// </summary>
public static class NginxConfigReader
{
    /// <summary>Header line the listing script prints before each site file: marker, enabled flag (0/1), name.</summary>
    public const string SiteMarker = "@@LRW-SITE@@";

    private static readonly Regex LetsEncryptCert = new(@"^/etc/letsencrypt/live/([^/]+)/");

    public static List<NginxSite> ParseListing(string listing)
    {
        var sites = new List<NginxSite>();
        string? name = null;
        var enabled = false;
        var content = new StringBuilder();

        void Flush()
        {
            if (name != null)
                sites.Add(ParseSite(name, enabled, content.ToString()));
            content.Clear();
        }

        foreach (var line in listing.Split('\n'))
        {
            if (line.StartsWith(SiteMarker + " ", StringComparison.Ordinal))
            {
                Flush();
                var header = line[(SiteMarker.Length + 1)..].TrimEnd('\r');
                var space = header.IndexOf(' ');
                if (space < 0) { name = null; continue; }
                enabled = header[..space] == "1";
                name = header[(space + 1)..];
            }
            else
            {
                content.Append(line).Append('\n');
            }
        }
        Flush();

        return sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static NginxSite ParseSite(string name, bool enabled, string content)
    {
        var serverNames = new List<string>();
        var ports = new SortedSet<int>();
        var certNames = new List<string>();
        var https = false;
        string? proxyPass = null, root = null, redirect = null;

        foreach (var (directive, args) in Statements(content))
        {
            switch (directive)
            {
                case "server_name":
                    foreach (var n in args)
                        if (!serverNames.Contains(n)) serverNames.Add(n);
                    break;
                case "listen" when args.Length > 0:
                    if (ListenPort(args[0]) is { } port) ports.Add(port);
                    if (args.Contains("ssl")) https = true;
                    break;
                case "ssl_certificate" when args.Length > 0:
                    https = true;
                    var m = LetsEncryptCert.Match(args[0]);
                    if (m.Success && !certNames.Contains(m.Groups[1].Value)) certNames.Add(m.Groups[1].Value);
                    break;
                case "proxy_pass" when args.Length > 0:
                    proxyPass ??= args[0];
                    break;
                case "root" when args.Length > 0:
                    root ??= args[0];
                    break;
                case "return" when args.Length > 1:
                    redirect ??= args[^1];
                    break;
            }
        }

        var target = proxyPass != null ? $"proxy → {proxyPass}"
            : root != null ? $"static {root}"
            : redirect != null ? $"redirect → {redirect}"
            : string.Empty;

        var header = string.Join('\n', content.Split('\n').Take(3));
        var createdByApp = header.Contains(SiteTemplate.CreatedByMarker, StringComparison.Ordinal);

        return new NginxSite(name, enabled, serverNames, ports.Select(p => p.ToString()).ToList(),
            https, certNames, target, createdByApp);
    }

    /// <summary>"80", "[::]:443", "127.0.0.1:8080", "localhost" → port; unix sockets → null.</summary>
    internal static int? ListenPort(string address)
    {
        if (address.StartsWith("unix:", StringComparison.Ordinal)) return null;
        var colon = address.LastIndexOf(':');
        var tail = colon >= 0 ? address[(colon + 1)..] : address;
        if (int.TryParse(tail, out var port)) return port;
        // "listen localhost;" or "listen 10.0.0.1;" — nginx defaults to port 80
        return colon < 0 || address.EndsWith(']') ? 80 : null;
    }

    /// <summary>Splits config text into (directive, arguments) statements. Blocks are flattened.</summary>
    internal static IEnumerable<(string Directive, string[] Args)> Statements(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quote = '\0';
        var comment = false;

        void EndWord()
        {
            if (inWord) words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (comment)
            {
                if (c == '\n') comment = false;
                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length) word.Append(text[++i]);
                else if (c == quote) quote = '\0';
                else word.Append(c);
                continue;
            }

            switch (c)
            {
                case '#' when !inWord:
                    comment = true;
                    break;
                case '"' or '\'':
                    quote = c;
                    inWord = true;
                    break;
                case ';' or '{' or '}':
                    EndWord();
                    if (words.Count > 0)
                        yield return (words[0], words.Skip(1).ToArray());
                    words.Clear();
                    break;
                case ' ' or '\t' or '\r' or '\n':
                    EndWord();
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }
    }
}
