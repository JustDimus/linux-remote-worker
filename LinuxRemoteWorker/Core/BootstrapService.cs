namespace LinuxRemoteWorker.Core;

/// <summary>
/// Ensures the program-owned root and service user exist on the server.
/// Idempotent — safe to run on every action.
/// </summary>
public class BootstrapService
{
    private readonly SshService _ssh;

    public BootstrapService(SshService ssh)
    {
        _ssh = ssh;
    }

    public async Task EnsureAsync()
    {
        var script = string.Join(" && ", new[]
        {
            // dedicated non-login service user
            $"id {DeployPaths.ServiceUser} >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin {DeployPaths.ServiceUser}",
            // directory tree
            $"mkdir -p {DeployPaths.Repos} {DeployPaths.Apps} {DeployPaths.Keys}",
            $"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {DeployPaths.Base}",
            $"chmod 700 {DeployPaths.Keys}",
            // git commands run as root over repos owned by lrw — trust them (idempotent)
            $"git config --global --get-all safe.directory 2>/dev/null | grep -qx '{DeployPaths.Repos}/*' || git config --global --add safe.directory '{DeployPaths.Repos}/*'"
        });

        await _ssh.RunCommandAsync(script);
    }
}
