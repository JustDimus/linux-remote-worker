using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.SystemInfo;

public partial class SystemInfoViewModel : BaseViewModel, IModule
{
    public string Title => "System Info";
    public string Icon => "🖥";

    private readonly SystemInfoService _system;

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
        _system = new SystemInfoService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunSafeAsync(async () =>
        {
            SetStatus("Loading system info...");

            var o = await _system.GetOverviewAsync();
            Hostname = o.Hostname;
            Os = o.Os;
            Kernel = o.Kernel;
            Uptime = o.Uptime;
            CpuUsage = o.CpuBusyPercent is { } cpu ? $"{cpu.ToString(CultureInfo.InvariantCulture)}%" : "-";
            MemoryInfo = o.Memory;
            DiskInfo = o.Disk;
            IpAddress = o.PrimaryIp;

            var n = await _system.GetNetworkAsync(probeConnectivity: false);
            Ipv4Addresses = n.Ipv4Addresses.Count == 0 ? "(none — IPv6-only)" : string.Join("\n", n.Ipv4Addresses);
            Ipv6Addresses = n.Ipv6Addresses.Count == 0 ? "(none)" : string.Join("\n", n.Ipv6Addresses);
            GatewayV4 = n.GatewayV4 ?? "(none)";
            GatewayV6 = n.GatewayV6 ?? "(none)";
            DnsServers = n.DnsServers.Count == 0 ? "-" : string.Join(" ", n.DnsServers);

            // The outbound probes take up to 8 seconds, so everything else is on screen before them
            var (ipv4, ipv6) = await _system.ProbeConnectivityAsync();
            Connectivity = $"IPv4 out: {(ipv4 ? "✓" : "✗")}    IPv6 out: {(ipv6 ? "✓" : "✗")}";

            SetStatus("Updated");
        });
    }
}
