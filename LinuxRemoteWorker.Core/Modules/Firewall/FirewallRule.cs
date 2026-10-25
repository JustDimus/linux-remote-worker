namespace LinuxRemoteWorker.Modules.Firewall;

/// <summary>
/// One ufw rule as shown in the list. <see cref="To"/>, <see cref="Direction"/> and the raw
/// <see cref="From"/> identify it exactly, so it can be deleted by number together with its (v6) twin.
/// </summary>
public record FirewallRule(string Port, string Proto, string From, string Action, bool IsProtected = false)
{
    public string To { get; init; } = string.Empty;
    public string Direction { get; init; } = "IN";
}
