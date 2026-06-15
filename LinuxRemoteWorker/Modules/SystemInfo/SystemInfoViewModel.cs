using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.SystemInfo;

public partial class SystemInfoViewModel : BaseViewModel, IModule
{
    public string Title => "System Info";
    public string Icon => "🖥";

    private readonly SshService _ssh;

    [ObservableProperty] private string _hostname = "-";
    [ObservableProperty] private string _os = "-";
    [ObservableProperty] private string _kernel = "-";
    [ObservableProperty] private string _uptime = "-";
    [ObservableProperty] private string _cpuUsage = "-";
    [ObservableProperty] private string _memoryInfo = "-";
    [ObservableProperty] private string _diskInfo = "-";
    [ObservableProperty] private string _ipAddress = "-";

    // Network
    [ObservableProperty] private string _ipv4Addresses = "-";
    [ObservableProperty] private string _ipv6Addresses = "-";
    [ObservableProperty] private string _gatewayV4 = "-";
    [ObservableProperty] private string _gatewayV6 = "-";
    [ObservableProperty] private string _dnsServers = "-";
    [ObservableProperty] private string _connectivity = "-";

    public SystemInfoViewModel(SshService ssh)
    {
        _ssh = ssh;
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunSafeAsync(async () =>
        {
            SetStatus("Loading system info...");

            var hostname = await _ssh.RunCommandAsync("hostname");
            var os = await _ssh.RunCommandAsync("cat /etc/os-release | grep PRETTY_NAME | cut -d= -f2 | tr -d '\"'");
            var kernel = await _ssh.RunCommandAsync("uname -r");
            var uptime = await _ssh.RunCommandAsync("uptime -p");
            var cpu = await _ssh.RunCommandAsync("top -bn1 | grep 'Cpu(s)' | awk '{print $2}' | cut -d'%' -f1");
            var mem = await _ssh.RunCommandAsync("free -h | awk '/Mem:/ {print $3 \" used / \" $2 \" total\"}'");
            var disk = await _ssh.RunCommandAsync("df -h / | awk 'NR==2 {print $3 \" used / \" $2 \" total (\" $5 \")\"}'");
            var ip = await _ssh.RunCommandAsync("hostname -I | awk '{print $1}'");

            Hostname = hostname;
            Os = os;
            Kernel = kernel;
            Uptime = uptime;
            CpuUsage = string.IsNullOrEmpty(cpu) ? "-" : $"{cpu}%";
            MemoryInfo = mem;
            DiskInfo = disk;
            IpAddress = ip;

            await LoadNetworkAsync();

            SetStatus("Updated");
        });
    }

    private async Task LoadNetworkAsync()
    {
        var v4 = await _ssh.RunCommandAsync(
            "ip -4 -o addr show scope global 2>/dev/null | awk '{print $2\"  \"$4}'");
        Ipv4Addresses = string.IsNullOrWhiteSpace(v4) ? "(none — IPv6-only)" : v4.Trim();

        var v6 = await _ssh.RunCommandAsync(
            "ip -6 -o addr show scope global 2>/dev/null | awk '{print $2\"  \"$4}'");
        Ipv6Addresses = string.IsNullOrWhiteSpace(v6) ? "(none)" : v6.Trim();

        var gw4 = await _ssh.RunCommandAsync("ip -4 route show default 2>/dev/null | awk '{print $3}' | head -1");
        GatewayV4 = string.IsNullOrWhiteSpace(gw4) ? "(none)" : gw4.Trim();

        var gw6 = await _ssh.RunCommandAsync("ip -6 route show default 2>/dev/null | awk '{print $3}' | head -1");
        GatewayV6 = string.IsNullOrWhiteSpace(gw6) ? "(none)" : gw6.Trim();

        var dns = await _ssh.RunCommandAsync(
            "resolvectl status 2>/dev/null | grep -m1 'DNS Servers' | sed 's/.*DNS Servers: //' " +
            "|| grep '^nameserver' /etc/resolv.conf | awk '{print $2}' | paste -sd ' '");
        DnsServers = string.IsNullOrWhiteSpace(dns) ? "-" : dns.Trim();

        // Quick outbound reachability probe (4s each)
        var v4ok = (await _ssh.RunCommandAsync(
            "curl -4 -s -m 4 -o /dev/null -w 'ok' https://github.com 2>/dev/null || echo no")).Trim() == "ok";
        var v6ok = (await _ssh.RunCommandAsync(
            "curl -6 -s -m 4 -o /dev/null -w 'ok' https://ipv6.google.com 2>/dev/null || echo no")).Trim() == "ok";
        Connectivity = $"IPv4 out: {(v4ok ? "✓" : "✗")}    IPv6 out: {(v6ok ? "✓" : "✗")}";
    }
}
