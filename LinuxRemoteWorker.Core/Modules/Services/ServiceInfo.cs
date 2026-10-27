namespace LinuxRemoteWorker.Modules.Services;

/// <summary>A deployed service; <see cref="Deployment"/> is null for services deployed before versions were recorded.</summary>
public record ServiceInfo(string AppName, string UnitName, bool IsActive, bool IsEnabled, DeploymentInfo? Deployment = null)
{
    public string StatusText => (IsActive ? "active" : "inactive") + " · " + (IsEnabled ? "enabled" : "disabled");

    /// <summary>"v1.4.2 (8b1c0de) from shop-prod · Production · …", or empty when unknown.</summary>
    public string VersionText => Deployment?.Describe() ?? string.Empty;
}
