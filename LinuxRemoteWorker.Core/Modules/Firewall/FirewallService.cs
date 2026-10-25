using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Firewall;

/// <summary>
/// ufw state as the Firewall screen shows it. <see cref="Rules"/> starts with a protected entry for the
/// SSH port this session uses; SSH allow rules themselves are folded into it.
/// </summary>
public sealed record FirewallStatus(bool Enabled, IReadOnlyList<string> SshPorts, IReadOnlyList<FirewallRule> Rules);

/// <summary>
/// ufw operations with the safety rules of the Firewall screen: every port sshd listens on is allowed
/// before the firewall is enabled, and nothing that lets SSH in can be denied or deleted.
/// </summary>
public sealed class FirewallService
{
    private const string NumberedStatusCommand = "ufw status numbered 2>/dev/null";

    private readonly SshService _ssh;

    public FirewallService(SshService ssh)
    {
        _ssh = ssh;
    }

    /// <summary>Every port sshd listens on; the first one is the port this session came in on.</summary>
    public async Task<List<string>> ReadSshPortsAsync()
    {
        var ports = (await _ssh.ExecuteAsync(Ufw.SshPortsCommand)).Lines;
        return ports.Count > 0 ? ports : ["22"];
    }

    /// <param name="sshPorts">Pass the ports when they were just read, to save a round trip.</param>
    public async Task<FirewallStatus> GetStatusAsync(IReadOnlyList<string>? sshPorts = null)
    {
        sshPorts ??= await ReadSshPortsAsync();
        // "Status: active" heads the numbered listing too - one call gives both
        var numbered = (await _ssh.ExecuteAsync(NumberedStatusCommand)).Output;
        var enabled = numbered.TrimStart().StartsWith("Status: active", StringComparison.OrdinalIgnoreCase);
        return new FirewallStatus(enabled, sshPorts, BuildRules(enabled ? Ufw.ParseNumbered(numbered) : [], sshPorts));
    }

    /// <summary>The protected SSH entry first, then every rule except those that already let SSH in.</summary>
    public static List<FirewallRule> BuildRules(IReadOnlyList<UfwEntry> entries, IReadOnlyList<string> sshPorts)
    {
        var rules = new List<FirewallRule>
        {
            new(Port: sshPorts.Count > 0 ? sshPorts[0] : "22", Proto: "tcp", From: "any", Action: "allow", IsProtected: true)
        };
        rules.AddRange(Ufw.ToRules(entries).Where(rule => !IsSshAllow(rule, sshPorts)));
        return rules;
    }

    /// <summary>Any rule that lets SSH in (port, range, list or the OpenSSH profile) is protected.</summary>
    public static bool IsSshAllow(FirewallRule rule, IReadOnlyList<string> sshPorts) =>
        rule.Action is "allow" or "limit" &&
        sshPorts.Any(p => int.TryParse(p, out var port) && Ufw.CoversTcpPort(rule, port));

    /// <summary>Allows every SSH port, then enables ufw. Returns the SSH ports that were allowed.</summary>
    public async Task<IReadOnlyList<string>> EnableAsync(Action<string>? progress = null)
    {
        progress?.Invoke("Reading SSH config...");
        var sshPorts = await ReadSshPortsAsync();

        // ALWAYS allow SSH first — every port sshd uses, starting with this session's
        foreach (var port in sshPorts)
        {
            progress?.Invoke($"Allowing SSH (port {port}) before enabling firewall...");
            var allowed = await _ssh.ExecuteAsync($"ufw allow {port}/tcp 2>&1");
            if (!allowed.Succeeded)
                throw OperationException.Failed($"Could not allow SSH port {port}, firewall NOT enabled: {allowed.Text}");
        }

        progress?.Invoke("Enabling UFW...");
        var r = await _ssh.ExecuteAsync("ufw --force enable 2>&1");
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return sshPorts;
    }

    public async Task DisableAsync()
    {
        var r = await _ssh.ExecuteAsync("ufw disable 2>&1");
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }

    /// <summary>Adds an allow/deny rule and returns ufw's answer. A deny that covers an SSH port is refused.</summary>
    public async Task<string> AddRuleAsync(string action, string port, string proto, string from)
    {
        var (command, error) = Ufw.AddCommand(action, port, proto, from);
        if (command == null) throw OperationException.Invalid(error!);

        // Re-read actual SSH ports before applying anything
        var sshPorts = await ReadSshPortsAsync();
        if (action == "deny")
            foreach (var sshPort in sshPorts)
                CheckNotBlockingSsh(port.Trim(), sshPort);

        var r = await _ssh.ExecuteAsync(command);
        if (!r.Succeeded || r.Text.StartsWith("ERROR", StringComparison.Ordinal))
            throw OperationException.Failed(r.Text);
        return r.Text;
    }

    // Throws if the port spec would block the SSH port
    private static void CheckNotBlockingSsh(string portSpec, string sshPort)
    {
        if (!int.TryParse(sshPort, out var ssh)) return;

        // Exact match: "22"
        if (portSpec == sshPort)
            throw OperationException.Refused($"Port {sshPort} is the active SSH port — cannot deny it.");

        // Range match: "1:100"
        if (portSpec.Contains(':'))
        {
            var parts = portSpec.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var from)
                && int.TryParse(parts[1], out var to)
                && ssh >= from && ssh <= to)
                throw OperationException.Refused($"Range {portSpec} includes SSH port {sshPort} — cannot deny it.");
        }
    }

    /// <summary>Deletes a rule and its IPv6 twin by number. Rules that let SSH in are refused.</summary>
    public async Task DeleteRuleAsync(FirewallRule rule)
    {
        if (rule.IsProtected)
            throw OperationException.Refused("SSH rule is protected and cannot be removed.");

        // Re-read SSH ports before deleting — double-check it's not SSH
        var sshPorts = await ReadSshPortsAsync();
        if (IsSshAllow(rule, sshPorts))
            throw OperationException.Refused($"Cannot remove the SSH allow rule for port {rule.Port}.");

        // Rule numbers change with every edit, so look them up right before deleting
        var entries = Ufw.ParseNumbered((await _ssh.ExecuteAsync(NumberedStatusCommand)).Output);
        var numbers = Ufw.NumbersOf(rule, entries);
        if (numbers.Count == 0)
            throw OperationException.NotFound("That rule no longer exists.");

        var r = await _ssh.ExecuteAsync(Ufw.DeleteCommand(numbers));
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }
}
