namespace LinuxRemoteWorker.Core;

/// <summary>
/// Single program-owned root on every managed server.
/// </summary>
public static class DeployPaths
{
    public const string Base = "/srv/lrw";
    public const string Repos = Base + "/repos";
    public const string Apps = Base + "/apps";
    public const string Keys = Base + "/keys";
    public const string Logs = Base + "/logs";

    public const string ServiceUser = "lrw";

    // SSH deploy key used for git access
    public const string GitKey = Keys + "/git_deploy";
    public const string GitKeyPub = GitKey + ".pub";

    // systemd unit prefix so the program can tell its own services apart
    public const string ServicePrefix = "lrw-";

    public static string RepoDir(string name) => $"{Repos}/{name}";
    public static string AppDir(string name) => $"{Apps}/{name}";
    public static string LogDir(string app) => $"{Logs}/{app}";
    public static string UnitName(string app) => $"{ServicePrefix}{app}.service";
    public static string UnitPath(string app) => $"/etc/systemd/system/{UnitName(app)}";

    // GIT_SSH_COMMAND prefix to clone/fetch with the deploy key
    public static string GitSshEnv =>
        $"GIT_SSH_COMMAND='ssh -i {GitKey} -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes'";

    // nginx (Debian/Ubuntu package layout) — system locations, not program-owned
    public const string NginxConf = "/etc/nginx/nginx.conf";
    public const string NginxSitesAvailable = "/etc/nginx/sites-available";
    public const string NginxSitesEnabled = "/etc/nginx/sites-enabled";
    public const string NginxConfD = "/etc/nginx/conf.d";
    public const string NginxLogs = "/var/log/nginx";
    public const string WebRoot = "/var/www";

    // http{}-level map shared by proxied sites; lrw_ variable name so it cannot clash with a user's own map
    public const string NginxWebSocketMap = NginxConfD + "/lrw-websocket.conf";

    // Let's Encrypt (certbot)
    public const string LetsEncryptLive = "/etc/letsencrypt/live";

    public static string SiteAvailablePath(string site) => $"{NginxSitesAvailable}/{site}";
    public static string SiteEnabledPath(string site) => $"{NginxSitesEnabled}/{site}";
    public static string SiteWebRoot(string site) => $"{WebRoot}/{site}";
    public static string CertLiveDir(string certName) => $"{LetsEncryptLive}/{certName}";
}
