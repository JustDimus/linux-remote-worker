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

    private readonly FirewallService _firewall;

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
        _firewall = new FirewallService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await WorkAsync(ReloadFromServerAsync);

    // Full re-read from server: SSH ports, status and rules
    private async Task ReloadFromServerAsync()
    {
        var status = await _firewall.GetStatusAsync();
        IsEnabled = status.Enabled;
        SshPort = status.SshPorts[0];
        Rules.Clear();
        foreach (var rule in status.Rules) Rules.Add(rule);
    }

    [RelayCommand]
    private async Task EnableAsync() => await WorkAsync(async () =>
    {
        var sshPorts = await ThenReloadAsync(() => _firewall.EnableAsync(message => SetStatus(message)), ReloadFromServerAsync);
        SetStatus($"Firewall enabled. SSH port {string.Join(", ", sshPorts)} is allowed.");
    });

    [RelayCommand]
    private async Task DisableAsync() => await WorkAsync(async () =>
    {
        SetStatus("Disabling UFW...");
        await ThenReloadAsync(_firewall.DisableAsync, ReloadFromServerAsync);
        SetStatus("Firewall disabled.");
    });

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        // Rejected input never reaches the server, and the form keeps what was typed
        var (command, error) = Ufw.AddCommand(NewAction, NewPort, NewProto, NewFrom);
        if (command == null)
        {
            SetStatus(error!, isError: true);
            return;
        }

        await WorkAsync(async () =>
        {
            SetStatus("Reading SSH config...");
            var result = await ThenReloadAsync(() => _firewall.AddRuleAsync(NewAction, NewPort, NewProto, NewFrom), ReloadFromServerAsync);
            NewPort = string.Empty;
            NewFrom = "any";
            SetStatus($"Rule added: {result}");
        });
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(FirewallRule rule)
    {
        if (rule.IsProtected)
        {
            SetStatus("SSH rule is protected and cannot be removed.", isError: true);
            return;
        }

        await WorkAsync(async () =>
        {
            await ThenReloadAsync(() => _firewall.DeleteRuleAsync(rule), ReloadFromServerAsync);
            SetStatus("Rule removed");
        });
    }

    private async Task WorkAsync(Func<Task> action)
    {
        IsBusyFirewall = true;
        await RunSafeAsync(action);
        IsBusyFirewall = false;
    }
}
