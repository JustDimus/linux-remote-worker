using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Firewall;

public partial class FirewallViewModel : BaseViewModel, IModule
{
    public string Title => "Firewall";
    public string Icon => "🛡";

    private readonly SshService _ssh;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusyFirewall;
    [ObservableProperty] private string _sshPort = "22";
    [ObservableProperty] private string _newPort = string.Empty;
    [ObservableProperty] private string _newProto = "tcp";
    [ObservableProperty] private string _newFrom = "any";
    [ObservableProperty] private string _newAction = "allow";

    public List<string> ProtoOptions { get; } = ["tcp", "udp", "any"];
    public List<string> ActionOptions { get; } = ["allow", "deny"];

    public ObservableCollection<FirewallRule> Rules { get; } = [];

    public FirewallViewModel(SshService ssh)
    {
        _ssh = ssh;
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyFirewall = true;
        await RunSafeAsync(ReloadFromServerAsync);
        IsBusyFirewall = false;
    }

    private async Task<string> ReadSshPortFromServerAsync()
    {
        var portLine = await _ssh.RunCommandAsync(
            "grep -E '^Port ' /etc/ssh/sshd_config 2>/dev/null | awk '{print $2}' | head -1");
        return string.IsNullOrWhiteSpace(portLine) ? "22" : portLine.Trim();
    }

    // Full re-read from server: status + SSH port + rules
    private async Task ReloadFromServerAsync()
    {
        SshPort = await ReadSshPortFromServerAsync();
        var status = await _ssh.RunCommandAsync("ufw status 2>/dev/null | head -1");
        IsEnabled = status.Trim().Equals("Status: active", StringComparison.OrdinalIgnoreCase);
        await LoadRulesAsync();
    }

    private async Task LoadRulesAsync()
    {
        Rules.Clear();

        // Always show SSH as protected rule first (using actual port)
        Rules.Add(new FirewallRule(Port: SshPort, Proto: "tcp", From: "any", Action: "allow", IsProtected: true));

        if (!IsEnabled) return;

        var output = await _ssh.RunCommandAsync("ufw status numbered 2>/dev/null");
        foreach (var line in output.Split('\n'))
        {
            // Parse lines like: [ 1] 22/tcp                     ALLOW IN    Anywhere
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("[")) continue;

            var content = trimmed.TrimStart('[', ' ');
            var bracketEnd = content.IndexOf(']');
            if (bracketEnd < 0) continue;
            content = content[(bracketEnd + 1)..].Trim();

            var parts = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var portProto = parts[0];
            var action = parts[1].ToLower() == "allow" ? "allow" : "deny";
            var from = parts.Length >= 4 ? parts[^1] : "any";

            var port = portProto.Contains('/') ? portProto.Split('/')[0] : portProto;
            var proto = portProto.Contains('/') ? portProto.Split('/')[1] : "any";

            // Skip SSH — already shown as protected
            if (port == SshPort && proto is "tcp" or "any") continue;
            // Skip IPv6 duplicates
            if (from.Contains("(v6)") || port.Contains("(v6)")) continue;

            Rules.Add(new FirewallRule(port, proto, from, action));
        }
    }

    [RelayCommand]
    private async Task EnableAsync()
    {
        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Reading SSH config...");
            SshPort = await ReadSshPortFromServerAsync();

            SetStatus($"Allowing SSH (port {SshPort}) before enabling firewall...");
            // ALWAYS allow SSH first — no exceptions
            await _ssh.RunCommandAsync($"ufw allow {SshPort}/tcp");

            SetStatus("Enabling UFW...");
            await _ssh.RunCommandAsync("ufw --force enable");

            await ReloadFromServerAsync();
            SetStatus($"Firewall enabled. SSH port {SshPort} is allowed.");
        });
        IsBusyFirewall = false;
    }

    [RelayCommand]
    private async Task DisableAsync()
    {
        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Disabling UFW...");
            await _ssh.RunCommandAsync("ufw disable");
            await ReloadFromServerAsync();
            SetStatus("Firewall disabled.");
        });
        IsBusyFirewall = false;
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPort))
        {
            SetStatus("Enter a port number", isError: true);
            return;
        }

        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            // Re-read actual SSH port before applying anything
            SetStatus("Reading SSH config...");
            SshPort = await ReadSshPortFromServerAsync();

            if (NewAction == "deny")
                CheckNotBlockingSsh(NewPort.Trim(), SshPort);

            var fromPart = NewFrom is "any" or "" ? "" : $"from {NewFrom} to any";
            var protoPart = NewProto == "any" ? NewPort : $"{NewPort}/{NewProto}";
            var cmd = string.IsNullOrWhiteSpace(fromPart)
                ? $"ufw {NewAction} {protoPart}"
                : $"ufw {NewAction} {fromPart} port {NewPort} proto {NewProto}";

            var result = await _ssh.RunCommandAsync(cmd);
            NewPort = string.Empty;
            NewFrom = "any";
            await ReloadFromServerAsync();
            SetStatus($"Rule added: {result.Trim()}");
        });
        IsBusyFirewall = false;
    }

    // Throws if the port spec would block the SSH port
    private static void CheckNotBlockingSsh(string portSpec, string sshPort)
    {
        if (!int.TryParse(sshPort, out var ssh)) return;

        // Exact match: "22"
        if (portSpec == sshPort)
            throw new Exception($"Port {sshPort} is the active SSH port — cannot deny it.");

        // Range match: "1:100"
        if (portSpec.Contains(':'))
        {
            var parts = portSpec.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var from)
                && int.TryParse(parts[1], out var to)
                && ssh >= from && ssh <= to)
                throw new Exception($"Range {portSpec} includes SSH port {sshPort} — cannot deny it.");
        }
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(FirewallRule rule)
    {
        if (rule.IsProtected)
        {
            SetStatus("SSH rule is protected and cannot be removed.", isError: true);
            return;
        }

        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            // Re-read SSH port before deleting — double-check it's not SSH
            SshPort = await ReadSshPortFromServerAsync();
            if (rule.Port == SshPort && rule.Proto is "tcp" or "any" && rule.Action == "allow")
                throw new Exception($"Cannot remove SSH allow rule for port {SshPort}.");

            var proto = rule.Proto == "any" ? rule.Port : $"{rule.Port}/{rule.Proto}";
            await _ssh.RunCommandAsync($"ufw delete {rule.Action} {proto}");
            await ReloadFromServerAsync();
            SetStatus("Rule removed");
        });
        IsBusyFirewall = false;
    }
}
