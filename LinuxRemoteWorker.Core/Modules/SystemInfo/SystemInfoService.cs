using System.Globalization;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.SystemInfo;

/// <summary>A one-shot picture of the server: identity, load, memory and disk, then the network.</summary>
public sealed record SystemSnapshot(SystemOverview Overview, NetworkSnapshot Network);

/// <summary>Identity and load. CpuBusyPercent is the busy share of all CPU time over one second.</summary>
public sealed record SystemOverview(
    string Hostname,
    string Os,
    string Kernel,
    string Uptime,
    double? CpuBusyPercent,
    string Memory,
    string Disk,
    string PrimaryIp);

/// <summary>Addresses ("eth0  203.0.113.10/24"), default routes, resolvers, and whether outbound traffic works (null when not probed).</summary>
public sealed record NetworkSnapshot(
    IReadOnlyList<string> Ipv4Addresses,
    IReadOnlyList<string> Ipv6Addresses,
    string? GatewayV4,
    string? GatewayV6,
    IReadOnlyList<string> DnsServers,
    bool? Ipv4Outbound,
    bool? Ipv6Outbound);

public sealed class SystemInfoService
{
    private readonly SshService _ssh;

    public SystemInfoService(SshService ssh)
    {
        _ssh = ssh;
    }

    /// <param name="probeConnectivity">Also try reaching the internet over IPv4 and IPv6 (up to 8 seconds).</param>
    public async Task<SystemSnapshot> GetAsync(bool probeConnectivity = true) =>
        new(await GetOverviewAsync(), await GetNetworkAsync(probeConnectivity));

    public async Task<SystemOverview> GetOverviewAsync()
    {
        var hostname = await _ssh.RunCommandAsync("hostname");
        var os = await _ssh.RunCommandAsync("cat /etc/os-release | grep PRETTY_NAME | cut -d= -f2 | tr -d '\"'");
        var kernel = await _ssh.RunCommandAsync("uname -r");
        var uptime = await _ssh.RunCommandAsync("uptime -p");
        // Busy share of all CPU time over one second (100 - idle); top's first field is user time only
        var cpu = await _ssh.RunCommandAsync("vmstat 1 2 2>/dev/null | tail -1 | awk '{print 100 - $15}'");
        var mem = await _ssh.RunCommandAsync("free -h | awk '/Mem:/ {print $3 \" used / \" $2 \" total\"}'");
        var disk = await _ssh.RunCommandAsync("df -h / | awk 'NR==2 {print $3 \" used / \" $2 \" total (\" $5 \")\"}'");
        var ip = await _ssh.RunCommandAsync("hostname -I | awk '{print $1}'");

        var cpuBusy = double.TryParse(cpu.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var busy)
            ? busy
            : (double?)null;

        return new SystemOverview(hostname, os, kernel, uptime, cpuBusy, mem, disk, ip);
    }

    public async Task<NetworkSnapshot> GetNetworkAsync(bool probeConnectivity = true)
    {
        var v4 = await _ssh.RunCommandAsync(
            "ip -4 -o addr show scope global 2>/dev/null | awk '{print $2\"  \"$4}'");
        var v6 = await _ssh.RunCommandAsync(
            "ip -6 -o addr show scope global 2>/dev/null | awk '{print $2\"  \"$4}'");
        var gw4 = await _ssh.RunCommandAsync("ip -4 route show default 2>/dev/null | awk '{print $3}' | head -1");
        var gw6 = await _ssh.RunCommandAsync("ip -6 route show default 2>/dev/null | awk '{print $3}' | head -1");

        // resolvectl first (systemd-resolved); resolv.conf when it is missing or lists no servers
        var dns = await _ssh.RunCommandAsync(
            "d=$(resolvectl status 2>/dev/null | grep -m1 'DNS Servers' | sed 's/.*DNS Servers: //'); " +
            "[ -n \"$d\" ] && echo \"$d\" || grep '^nameserver' /etc/resolv.conf | awk '{print $2}' | paste -sd ' '");

        var network = new NetworkSnapshot(
            Lines(v4), Lines(v6), NullIfBlank(gw4), NullIfBlank(gw6),
            dns.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            null, null);
        if (!probeConnectivity) return network;

        var (ipv4, ipv6) = await ProbeConnectivityAsync();
        return network with { Ipv4Outbound = ipv4, Ipv6Outbound = ipv6 };
    }

    /// <summary>Quick outbound reachability probe over IPv4 and IPv6 (up to 4 seconds each).</summary>
    public async Task<(bool Ipv4, bool Ipv6)> ProbeConnectivityAsync()
    {
        var v4ok = (await _ssh.RunCommandAsync(
            "curl -4 -s -m 4 -o /dev/null -w 'ok' https://github.com 2>/dev/null || echo no")).Trim() == "ok";
        var v6ok = (await _ssh.RunCommandAsync(
            "curl -6 -s -m 4 -o /dev/null -w 'ok' https://ipv6.google.com 2>/dev/null || echo no")).Trim() == "ok";
        return (v4ok, v6ok);
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
