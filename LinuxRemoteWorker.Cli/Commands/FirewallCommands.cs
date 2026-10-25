using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Firewall;

namespace LinuxRemoteWorker.Cli.Commands;

public enum RuleAction { Allow, Deny }

[CommandGroup("firewall", "ufw: status, enable/disable, add and delete rules - SSH is always kept open")]
public sealed class FirewallCommands : CommandSet
{
    public FirewallCommands(CommandContext context) : base(context) { }

    private async Task<FirewallService> FirewallAsync() => new(await SshAsync());

    [Command("status", "Whether ufw is active, every port sshd listens on, and the rules. The first rule is the protected SSH entry; rules that let SSH in are folded into it.",
        ReadOnly = true)]
    public async Task<FirewallStatus> Status() => await (await FirewallAsync()).GetStatusAsync();

    [Command("enable", "Enable ufw. Every port sshd listens on - starting with this session's - is allowed first, so you cannot lock yourself out.",
        Idempotent = true)]
    public async Task<Outcome> Enable()
    {
        var firewall = await FirewallAsync();
        var sshPorts = await firewall.EnableAsync(Lines);
        return new Outcome($"Firewall enabled. SSH port {string.Join(", ", sshPorts)} is allowed.") { Data = await firewall.GetStatusAsync() };
    }

    [Command("disable", "Disable ufw - every port is reachable again.", Destructive = true)]
    public async Task<Outcome> Disable()
    {
        var firewall = await FirewallAsync();
        await firewall.DisableAsync();
        return new Outcome("Firewall disabled.") { Data = await firewall.GetStatusAsync() };
    }

    [Command("add", "Allow or deny a port or range, optionally only from one address or network. A deny covering an SSH port is refused.",
        Example = "firewall add --port 5432 --from 10.0.0.0/24")]
    public async Task<Outcome> Add(
        [Arg("Port (5432) or range (8000:8100)")] string port,
        [Arg("tcp, udp or any (a range needs tcp or udp)", Choices = new[] { "tcp", "udp", "any" })] string proto = "tcp",
        [Arg("'any', an IP address or a network such as 10.0.0.0/24")] string from = "any",
        [Arg("allow or deny")] RuleAction action = RuleAction.Allow)
    {
        var firewall = await FirewallAsync();
        var result = await firewall.AddRuleAsync(action.ToString().ToLowerInvariant(), port, proto, from);
        return new Outcome($"Rule added: {result}") { Data = await firewall.GetStatusAsync() };
    }

    [Command("delete", "Delete the rule matching --port (and --proto / --from / --action when several match), together with its IPv6 twin. Rules that let SSH in cannot be deleted.",
        Destructive = true, Example = "firewall delete --port 5432 --from 10.0.0.0/24 --yes")]
    public async Task<Outcome> Delete(
        [Arg("Port, range or app profile as shown in status (e.g. 5432, 8000:8100, 'Nginx Full')")] string port,
        [Arg("tcp, udp, any or app", Choices = new[] { "tcp", "udp", "any", "app" })] string? proto = null,
        [Arg("Source as shown in status: 'any', an address or a network")] string? from = null,
        [Arg("allow, deny, reject or limit", Choices = new[] { "allow", "deny", "reject", "limit" })] string? action = null)
    {
        var firewall = await FirewallAsync();
        var status = await firewall.GetStatusAsync();
        var matches = status.Rules
            .Where(r => !r.IsProtected && r.Port == port.Trim())
            .Where(r => proto == null || r.Proto == proto)
            .Where(r => action == null || r.Action == action)
            .Where(r => from == null || SourceMatches(r.From, from))
            .ToList();

        var rule = matches.Count switch
        {
            0 => throw OperationException.NotFound($"No rule for {port} matches. See `lrw firewall status`."),
            1 => matches[0],
            _ => throw OperationException.Invalid(
                $"{matches.Count} rules match - narrow it down with --proto, --from or --action: " +
                string.Join("; ", matches.Select(r => $"{r.Port}/{r.Proto} {r.Action} from {r.From}")))
        };

        await firewall.DeleteRuleAsync(rule);
        return new Outcome($"Rule removed: {rule.Port}/{rule.Proto} {rule.Action} from {rule.From}") { Data = await firewall.GetStatusAsync() };
    }

    // ufw shows "Anywhere" for any, and may append a comment ("10.0.0.5   # db")
    private static bool SourceMatches(string ruleFrom, string from)
    {
        var shown = Ufw.StripV6(ruleFrom).Split(' ')[0];
        return from.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? shown.Equals("Anywhere", StringComparison.OrdinalIgnoreCase)
            : shown.Equals(from.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
