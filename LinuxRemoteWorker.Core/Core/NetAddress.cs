using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LinuxRemoteWorker.Core;

/// <summary>Strict parsing of IP addresses and CIDR networks typed into firewall and database rules.</summary>
public static class NetAddress
{
    // Dotted quad without leading zeros: IPAddress.TryParse would read "010.0.0.1" as octal 8.0.0.1
    private static readonly Regex Ipv4 = new(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$");

    /// <summary>"10.0.0.5", "10.0.0.0/24", "2001:db8::/32". <paramref name="prefix"/> is null without "/n".</summary>
    public static bool TryParseCidr(string text, out IPAddress address, out int? prefix)
    {
        address = IPAddress.None;
        prefix = null;

        var slash = text.IndexOf('/');
        var ip = slash < 0 ? text : text[..slash];
        var isV4 = Ipv4.IsMatch(ip);
        if (!(isV4 || ip.Contains(':')) || !IPAddress.TryParse(ip, out var parsed)) return false;
        if (!isV4 && parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;
        address = parsed;

        if (slash < 0) return true;
        if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits < 0 || bits > MaxPrefix(parsed)) return false;
        prefix = bits;
        return true;
    }

    public static int MaxPrefix(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
}
