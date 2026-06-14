namespace LinuxRemoteWorker.Modules.Firewall;

public record FirewallRule(string Port, string Proto, string From, string Action, bool IsProtected = false);
