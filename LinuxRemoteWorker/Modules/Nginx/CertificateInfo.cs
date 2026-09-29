using System.Globalization;

namespace LinuxRemoteWorker.Modules.Nginx;

/// <summary>A certificate lineage as reported by <c>certbot certificates</c>.</summary>
public record CertificateInfo(
    string Name,
    IReadOnlyList<string> Domains,
    DateTimeOffset? Expiry,
    string Validity,
    IReadOnlyList<string> UsedBy)
{
    public string DomainsText => string.Join("  ", Domains);

    public int? DaysLeft => Expiry is { } e ? (int)Math.Floor((e - DateTimeOffset.UtcNow).TotalDays) : null;

    public string DaysLeftText => DaysLeft switch
    {
        null => "?",
        < 0 => "expired",
        1 => "1 day left",
        var d => $"{d} days left"
    };

    public string ExpiryText => Expiry is { } e
        ? $"expires {e.ToLocalTime():yyyy-MM-dd}  ·  {Validity}"
        : Validity;

    public string UsedByText => UsedBy.Count == 0 ? "not used by any site" : "used by " + string.Join(", ", UsedBy);

    /// <summary>"ok", "soon" (inside certbot's 30-day renewal window) or "bad" — drives the colour in the view.</summary>
    public string Health => Validity.StartsWith("INVALID", StringComparison.OrdinalIgnoreCase) || DaysLeft is < 7
        ? "bad"
        : DaysLeft is < 30 ? "soon" : "ok";

    /// <summary>
    /// Parses the human-readable output of <c>certbot certificates</c>:
    /// <code>
    ///   Certificate Name: example.com
    ///     Domains: example.com www.example.com
    ///     Expiry Date: 2026-12-01 10:00:00+00:00 (VALID: 63 days)
    /// </code>
    /// </summary>
    public static List<CertificateInfo> ParseCertbotOutput(string output)
    {
        var result = new List<CertificateInfo>();
        string? name = null;
        List<string> domains = [];
        DateTimeOffset? expiry = null;
        var validity = string.Empty;

        void Flush()
        {
            if (name != null)
                result.Add(new CertificateInfo(name, domains, expiry, validity, []));
            name = null;
            domains = [];
            expiry = null;
            validity = string.Empty;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (TryValue(line, "Certificate Name:", out var v))
            {
                Flush();
                name = v;
            }
            else if (name != null && TryValue(line, "Domains:", out v))
            {
                domains = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else if (name != null && TryValue(line, "Expiry Date:", out v))
            {
                // "2026-12-01 10:00:00+00:00 (VALID: 63 days)"
                var paren = v.IndexOf(" (", StringComparison.Ordinal);
                var date = paren >= 0 ? v[..paren] : v;
                if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    expiry = parsed;
                validity = paren >= 0 ? v[(paren + 2)..].TrimEnd(')') : string.Empty;
            }
        }
        Flush();
        return result;
    }

    private static bool TryValue(string line, string key, out string value)
    {
        if (line.StartsWith(key, StringComparison.Ordinal))
        {
            value = line[key.Length..].Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }
}
