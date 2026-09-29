using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Firewall;

/// <summary>A line of <c>ufw status numbered</c>.</summary>
public record UfwEntry(int Number, string To, string Action, string Direction, string From)
{
    public bool IsV6 => To.Contains("(v6)", StringComparison.Ordinal);

    /// <summary>To/From without the "(v6)" marker — equal for a rule and its IPv6 twin.</summary>
    public string BaseTo => Ufw.StripV6(To);
    public string BaseFrom => Ufw.StripV6(From);
}

/// <summary>Parses ufw output and builds ufw commands from validated input.</summary>
public static class Ufw
{
    // "[ 3] 5432/tcp                   ALLOW IN    10.0.0.5"
    // "[ 4] 192.168.100.200 8000:8100/tcp ALLOW IN    Anywhere"      (long To: a single space)
    // "[ 5] 80/tcp (v6)                ALLOW IN    Anywhere (v6)              # web"
    private static readonly Regex NumberedLine = new(
        @"^\[\s*(?<n>\d+)\]\s+(?<to>.+?)\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\b(?:\s+(?<dir>IN|OUT|FWD)\b)?\s+(?<from>.+?)\s*$");

    private static readonly Regex PortToken = new(@"^(?<ports>\d{1,5}([,:]\d{1,5})*)(/(?<proto>[a-z]+))?$");
    private static readonly Regex PortSpec = new(@"^\d{1,5}(:\d{1,5})?$");

    // ufw pads the IPv4 and IPv6 lines differently ("Anywhere   # web" vs "Anywhere (v6)   # web"),
    // so whitespace runs are collapsed too
    public static string StripV6(string value) =>
        Regex.Replace(value.Replace(" (v6)", "", StringComparison.Ordinal), @"\s+", " ").Trim();

    public static List<UfwEntry> ParseNumbered(string output)
    {
        var entries = new List<UfwEntry>();
        foreach (var line in output.Split('\n'))
        {
            var m = NumberedLine.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            entries.Add(new UfwEntry(int.Parse(m.Groups["n"].Value), m.Groups["to"].Value.Trim(),
                m.Groups["action"].Value, m.Groups["dir"].Success ? m.Groups["dir"].Value : "IN",
                m.Groups["from"].Value.Trim()));
        }
        return entries;
    }

    /// <summary>
    /// The list shown on screen: one row per rule. An IPv4 rule and its "(v6)" twin become one row;
    /// an IPv6-only rule (e.g. from 2001:db8::/32) gets its own row.
    /// </summary>
    public static List<FirewallRule> ToRules(IReadOnlyList<UfwEntry> entries) =>
        entries.Where(e => !e.IsV6 || !entries.Any(o => !o.IsV6 && IsTwin(o, e))).Select(e =>
        {
            var to = e.BaseTo;
            var token = to.Split(' ').Select(t => PortToken.Match(t)).FirstOrDefault(m => m.Success);
            var simple = token != null && !to.Contains(' ');
            var port = simple ? token!.Groups["ports"].Value : to;
            var proto = token == null ? "app" : token.Groups["proto"].Success ? token.Groups["proto"].Value : "any";
            return new FirewallRule(port, proto, e.From, e.Action.ToLowerInvariant()) { To = e.To, Direction = e.Direction };
        }).ToList();

    private static bool IsTwin(UfwEntry a, UfwEntry b) =>
        a.Action == b.Action && a.Direction == b.Direction && a.BaseTo == b.BaseTo && a.BaseFrom == b.BaseFrom;

    /// <summary>
    /// ufw numbers of a rule and its (v6) twin, highest first — deleting from the end keeps the other
    /// number valid. Deleting by number works for every kind of rule (source-limited, app profiles,
    /// commented, outgoing), unlike re-typing the rule, which silently misses anything with "from".
    /// </summary>
    public static List<int> NumbersOf(FirewallRule rule, IEnumerable<UfwEntry> entries)
    {
        var target = new UfwEntry(0, rule.To, rule.Action.ToUpperInvariant(), rule.Direction, rule.From);
        return entries.Where(e => IsTwin(e, target)).Select(e => e.Number).OrderByDescending(n => n).ToList();
    }

    public static string DeleteCommand(IEnumerable<int> numbersHighestFirst) =>
        string.Join(" && ", numbersHighestFirst.Select(n => $"ufw --force delete {n} 2>&1"));

    /// <summary>True when the rule's To covers <paramref name="port"/> over TCP (ranges and lists included).</summary>
    public static bool CoversTcpPort(FirewallRule rule, int port)
    {
        if (rule.Proto == "app")
            return rule.Port.Equals("OpenSSH", StringComparison.OrdinalIgnoreCase) && port == 22;
        if (rule.Proto is not ("tcp" or "any")) return false;

        var token = StripV6(rule.To).Split(' ').Select(t => PortToken.Match(t)).FirstOrDefault(m => m.Success);
        if (token == null) return false;
        return token.Groups["ports"].Value.Split(',').Any(part =>
        {
            var range = part.Split(':').Select(int.Parse).ToArray();
            return range.Length == 1 ? range[0] == port : port >= range[0] && port <= range[1];
        });
    }

    /// <summary>Builds the ufw command for a new rule, or returns an error message.</summary>
    public static (string? Command, string? Error) AddCommand(string action, string port, string proto, string from)
    {
        port = port.Trim();
        from = string.IsNullOrWhiteSpace(from) ? "any" : from.Trim();

        if (action is not ("allow" or "deny")) return (null, "Action must be allow or deny.");
        if (proto is not ("tcp" or "udp" or "any")) return (null, "Protocol must be tcp, udp or any.");
        if (!PortSpec.IsMatch(port) || port.Split(':').Any(p => int.Parse(p) is < 1 or > 65535))
            return (null, "Port: a number (5432) or a range (8000:8100) between 1 and 65535.");
        if (port.Contains(':') && proto == "any")
            return (null, "A port range needs a protocol (tcp or udp).");
        if (from != "any" && !NetAddress.TryParseCidr(from, out _, out _))
            return (null, "From: 'any', an IP address or a network such as 10.0.0.0/24.");

        var protoPart = proto == "any" ? "" : $" proto {proto}";
        return from == "any"
            ? ($"ufw {action} {port}{(proto == "any" ? "" : "/" + proto)} 2>&1", null)
            : ($"ufw {action} from {from} to any port {port}{protoPart} 2>&1", null);
    }

    /// <summary>
    /// The SSH ports that must stay open: the one this very session came in on (from SSH_CONNECTION)
    /// first, then every port sshd is configured for (sshd -T also sees sshd_config.d drop-ins).
    /// </summary>
    public const string SshPortsCommand =
        "{ echo \"${SSH_CONNECTION##* }\"; sshd -T 2>/dev/null | awk '$1 == \"port\" {print $2}'; " +
        "grep -hE '^Port ' /etc/ssh/sshd_config 2>/dev/null | awk '{print $2}'; } | grep -E '^[0-9]+$' | awk '!seen[$0]++'";
}
