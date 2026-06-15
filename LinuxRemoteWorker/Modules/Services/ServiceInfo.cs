namespace LinuxRemoteWorker.Modules.Services;

public record ServiceInfo(string AppName, string UnitName, bool IsActive, bool IsEnabled)
{
    public string StatusText => (IsActive ? "active" : "inactive") + " · " + (IsEnabled ? "enabled" : "disabled");
}
