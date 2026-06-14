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

            SetStatus("Updated");
        });
    }
}
