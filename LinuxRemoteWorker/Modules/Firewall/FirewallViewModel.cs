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

    // Every port sshd listens on; the first one is the port this session came in on
    private List<string> _sshPorts = ["22"];

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

    private async Task ReadSshPortsAsync()
    {
        var ports = (await _ssh.ExecuteAsync(Ufw.SshPortsCommand)).Lines;
        _sshPorts = ports.Count > 0 ? ports : ["22"];
        SshPort = _sshPorts[0];
    }

    // Full re-read from server: status + rules (+ SSH ports unless the caller just read them)
    private async Task ReloadFromServerAsync() => await ReloadFromServerAsync(readSshPorts: true);

    private async Task ReloadFromServerAsync(bool readSshPorts)
    {
        if (readSshPorts) await ReadSshPortsAsync();
        // "Status: active" heads the numbered listing too - one call gives both
        var numbered = (await _ssh.ExecuteAsync("ufw status numbered 2>/dev/null")).Output;
        IsEnabled = numbered.TrimStart().StartsWith("Status: active", StringComparison.OrdinalIgnoreCase);
        LoadRules(IsEnabled ? Ufw.ParseNumbered(numbered) : []);
    }

    private async Task<List<UfwEntry>> ReadEntriesAsync() =>
        Ufw.ParseNumbered((await _ssh.ExecuteAsync("ufw status numbered 2>/dev/null")).Output);

    private void LoadRules(IReadOnlyList<UfwEntry> entries)
    {
        Rules.Clear();

        // Always show SSH as protected rule first (using the actual port)
        Rules.Add(new FirewallRule(Port: SshPort, Proto: "tcp", From: "any", Action: "allow", IsProtected: true));

        foreach (var rule in Ufw.ToRules(entries))
        {
            // SSH allow rules are already represented by the protected entry above
            if (IsSshAllow(rule)) continue;
            Rules.Add(rule);
        }
    }

    // Any rule that lets SSH in (port, range, list or the OpenSSH profile) is protected
    private bool IsSshAllow(FirewallRule rule) =>
        rule.Action is "allow" or "limit" &&
        _sshPorts.Any(p => int.TryParse(p, out var port) && Ufw.CoversTcpPort(rule, port));

    [RelayCommand]
    private async Task EnableAsync()
    {
        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            SetStatus("Reading SSH config...");
            await ReadSshPortsAsync();

            // ALWAYS allow SSH first — every port sshd uses, starting with this session's
            foreach (var port in _sshPorts)
            {
                SetStatus($"Allowing SSH (port {port}) before enabling firewall...");
                var allowed = await _ssh.ExecuteAsync($"ufw allow {port}/tcp 2>&1");
                if (!allowed.Succeeded)
                    throw new InvalidOperationException($"Could not allow SSH port {port}, firewall NOT enabled: {allowed.Text}");
            }

            SetStatus("Enabling UFW...");
            var r = await _ssh.ExecuteAsync("ufw --force enable 2>&1");
            await ReloadFromServerAsync(readSshPorts: false);
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);
            SetStatus($"Firewall enabled. SSH port {string.Join(", ", _sshPorts)} is allowed.");
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
            var r = await _ssh.ExecuteAsync("ufw disable 2>&1");
            await ReloadFromServerAsync();
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);
            SetStatus("Firewall disabled.");
        });
        IsBusyFirewall = false;
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var (command, error) = Ufw.AddCommand(NewAction, NewPort, NewProto, NewFrom);
        if (command == null)
        {
            SetStatus(error!, isError: true);
            return;
        }

        IsBusyFirewall = true;
        await RunSafeAsync(async () =>
        {
            // Re-read actual SSH ports before applying anything
            SetStatus("Reading SSH config...");
            await ReadSshPortsAsync();

            if (NewAction == "deny")
                foreach (var port in _sshPorts)
                    CheckNotBlockingSsh(NewPort.Trim(), port);

            var r = await _ssh.ExecuteAsync(command);
            await ReloadFromServerAsync(readSshPorts: false);
            if (!r.Succeeded || r.Text.StartsWith("ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException(r.Text);

            NewPort = string.Empty;
            NewFrom = "any";
            SetStatus($"Rule added: {r.Text}");
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
            // Re-read SSH ports before deleting — double-check it's not SSH
            await ReadSshPortsAsync();
            if (IsSshAllow(rule))
                throw new Exception($"Cannot remove the SSH allow rule for port {rule.Port}.");

            // Rule numbers change with every edit, so look them up right before deleting
            var numbers = Ufw.NumbersOf(rule, await ReadEntriesAsync());
            if (numbers.Count == 0)
            {
                await ReloadFromServerAsync(readSshPorts: false);
                throw new InvalidOperationException("That rule no longer exists - the list is refreshed.");
            }

            var r = await _ssh.ExecuteAsync(Ufw.DeleteCommand(numbers));
            await ReloadFromServerAsync(readSshPorts: false);
            if (!r.Succeeded) throw new InvalidOperationException(r.Text);
            SetStatus("Rule removed");
        });
        IsBusyFirewall = false;
    }
}
