using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Modules.Services;

public enum ServiceAction { Start, Stop, Restart }

/// <summary>
/// .NET apps published from a cloned repository to /srv/lrw/apps/&lt;app&gt; and run by systemd as
/// lrw-&lt;app&gt;.service under the lrw user.
/// </summary>
public sealed class AppServiceManager
{
    // The app name becomes a directory under /srv/lrw/apps and a systemd unit name, and is chown'ed
    // and deleted as root - so no slashes, no "..", nothing the shell or systemd would reinterpret.
    private static readonly Regex NewAppName = new(@"^[a-z0-9][a-z0-9_.-]{0,62}$");

    // Units that already exist may have been named by hand; they still must not carry shell syntax
    private static readonly Regex ExistingAppName = new(@"^[A-Za-z0-9][A-Za-z0-9_.@-]{0,99}$");

    private static readonly Regex UrlsPattern = new(@"^https?://\S+$");

    /// <summary>File in the app directory that records which .csproj it was published from.</summary>
    private const string SourceFileName = ".lrw-source";

    private readonly SshService _ssh;

    public AppServiceManager(SshService ssh)
    {
        _ssh = ssh;
    }

    // ── Validation ─────────────────────────────────────────────────────────

    public static string? ValidateNewAppName(string app) =>
        NewAppName.IsMatch(app) && !app.Contains("..")
            ? null
            : "App name: lower-case letters, digits, '.', '-' and '_' (for example shop-api).";

    public static string? ValidateAppName(string app) =>
        ExistingAppName.IsMatch(app) && !app.Contains("..") ? null : $"'{app}' is not a valid service name.";

    public static string? ValidateUrls(string urls) =>
        UrlsPattern.IsMatch(urls) ? null : "ASPNETCORE_URLS must look like http://0.0.0.0:5000 (no spaces).";

    /// <summary>Only projects inside a clone under /srv/lrw/repos are published.</summary>
    public static string? ValidateProject(string csproj) =>
        csproj.StartsWith(DeployPaths.Repos + "/", StringComparison.Ordinal)
        && csproj.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        && !csproj.Contains("..") && !csproj.Any(char.IsControl)
            ? null
            : $"The project must be a .csproj inside {DeployPaths.Repos}/ (see the projects of a repository).";

    /// <summary>"/srv/lrw/repos/shop/src/Shop.Api/Shop.Api.csproj" → "shop-api".</summary>
    public static string SuggestAppName(string csproj) =>
        csproj.Split('/').Last().Replace(".csproj", "").ToLower().Replace(".", "-");

    /// <summary>"lrw-shop-api.service" → "shop-api".</summary>
    public static string AppNameOf(string unitName)
    {
        var name = unitName.Trim();
        if (name.StartsWith(DeployPaths.ServicePrefix, StringComparison.Ordinal)) name = name[DeployPaths.ServicePrefix.Length..];
        if (name.EndsWith(".service", StringComparison.Ordinal)) name = name[..^".service".Length];
        return name;
    }

    // ── Reading ────────────────────────────────────────────────────────────

    /// <summary>Unit names of every service this app deployed (lrw-*.service).</summary>
    public async Task<List<string>> ListUnitNamesAsync()
    {
        var units = await _ssh.RunCommandAsync(
            $"systemctl list-unit-files '{DeployPaths.ServicePrefix}*.service' --no-legend 2>/dev/null | awk '{{print $1}}'");
        return units.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public async Task<List<ServiceInfo>> ListAsync()
    {
        var services = new List<ServiceInfo>();
        foreach (var unitName in await ListUnitNamesAsync())
            services.Add(await ReadInfoAsync(AppNameOf(unitName), unitName));
        return services;
    }

    public async Task<ServiceInfo> GetAsync(string app)
    {
        await EnsureExistsAsync(app);
        return await ReadInfoAsync(app, DeployPaths.UnitName(app));
    }

    private async Task<ServiceInfo> ReadInfoAsync(string app, string unitName)
    {
        var active = await _ssh.RunCommandAsync($"systemctl is-active {unitName} 2>/dev/null || echo inactive");
        var enabled = await _ssh.RunCommandAsync($"systemctl is-enabled {unitName} 2>/dev/null || echo disabled");
        return new ServiceInfo(app, unitName, active.Trim() == "active", enabled.Trim() == "enabled");
    }

    /// <summary>`systemctl status` of the service: state, main PID, memory and the last journal lines.</summary>
    public async Task<string> GetStatusTextAsync(string app, int journalLines = 10)
    {
        await EnsureExistsAsync(app);
        // exit 3 just means "not running" - the text is what matters
        return (await _ssh.ExecuteAsync($"systemctl status {DeployPaths.UnitName(app)} --no-pager -l -n {journalLines} 2>&1")).Text;
    }

    /// <summary>Clones under /srv/lrw/repos that a service can be published from.</summary>
    public async Task<List<string>> ListReposAsync()
    {
        var names = await _ssh.RunCommandAsync($"ls -1 {DeployPaths.Repos} 2>/dev/null");
        return names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Every .csproj in a clone (build output under obj/ excluded).</summary>
    public async Task<List<string>> FindProjectsAsync(string repo)
    {
        OperationException.ThrowIfInvalid(RepositoryService.ValidateExistingName(repo));

        var found = await _ssh.RunCommandAsync(
            $"find {Shell.Quote(DeployPaths.RepoDir(repo))} -name '*.csproj' -not -path '*/obj/*' 2>/dev/null");
        return found.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>The .csproj the app was last published from, or null when it was never deployed by this app.</summary>
    public async Task<string?> GetSourceProjectAsync(string app)
    {
        OperationException.ThrowIfInvalid(ValidateAppName(app));
        var csproj = (await _ssh.RunCommandAsync(
            $"cat {Shell.Quote(DeployPaths.AppDir(app) + "/" + SourceFileName)} 2>/dev/null")).Trim();
        return csproj.Length == 0 ? null : csproj;
    }

    public async Task<string> ReadUnitAsync(string app)
    {
        OperationException.ThrowIfInvalid(ValidateAppName(app));
        var r = await _ssh.ExecuteAsync($"cat {DeployPaths.UnitPath(app)} 2>/dev/null");
        if (!r.Succeeded) throw OperationException.NotFound($"No such service: {app} ({DeployPaths.UnitPath(app)} does not exist).");
        return r.Output.Trim();
    }

    // ── Deploying ──────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes <paramref name="csproj"/> to /srv/lrw/apps/&lt;app&gt;, writes the systemd unit, and
    /// enables and starts it. Re-deploying an existing app replaces its unit.
    /// </summary>
    public async Task DeployAsync(string csproj, string app, string urls, Action<string> onLine)
    {
        app = app.Trim();
        urls = urls.Trim();
        OperationException.ThrowIfInvalid(ValidateProject(csproj) ?? ValidateNewAppName(app) ?? ValidateUrls(urls));

        var appDir = DeployPaths.AppDir(app);
        var dotnetPath = await FindDotnetAsync();

        onLine($"→ Publishing {csproj}");
        onLine($"  → {appDir}");
        var published = await _ssh.ExecuteStreamAsync(
            $"{dotnetPath} publish {Shell.Quote(csproj)} -c Release -o {Shell.Quote(appDir)} 2>&1", onLine);
        if (!published.Succeeded)
            throw OperationException.Failed("dotnet publish failed - nothing was deployed. See the build output.", published.Text);

        // Resolve entry dll from runtimeconfig
        var rc = (await _ssh.RunCommandAsync(
            $"ls {appDir}/*.runtimeconfig.json 2>/dev/null | head -1")).Trim();
        if (string.IsNullOrWhiteSpace(rc))
            throw OperationException.Failed("Publish produced no runtimeconfig.json — is this an executable project?");
        var dll = rc.Split('/').Last().Replace(".runtimeconfig.json", ".dll");

        onLine($"\n→ Entry point: {dll}");
        onLine("→ Writing systemd unit...");

        await WriteUnitAsync(app, BuildUnit(app, appDir, dotnetPath, dll, urls));

        // Remember the source .csproj so Redeploy can rebuild without re-selecting
        await _ssh.RunCommandAsync($"printf '%s\\n' {Shell.Quote(csproj)} > {Shell.Quote(appDir + "/" + SourceFileName)}");

        await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(appDir)}");
        await _ssh.RunCommandAsync("systemctl daemon-reload");
        var started = await _ssh.ExecuteStreamAsync($"systemctl enable --now {DeployPaths.UnitName(app)} 2>&1", onLine);
        if (!started.Succeeded)
            throw OperationException.Failed($"The service was installed but did not start - check its journal. {started.Text}");
    }

    /// <summary>
    /// Re-publishes from the recorded .csproj and restarts; the unit is left untouched.
    /// With <paramref name="pull"/> the repository is pulled first.
    /// </summary>
    public async Task RedeployAsync(string app, bool pull, Action<string> onLine)
    {
        var csproj = await GetSourceProjectAsync(app)
                     ?? throw OperationException.NotFound("No recorded source for this service. Use Build & Deploy once to set it up.");
        var appDir = DeployPaths.AppDir(app);
        var unit = DeployPaths.UnitName(app);
        var dotnetPath = await FindDotnetAsync();

        if (pull)
        {
            var repoDir = RepoDirOf(csproj)
                          ?? throw OperationException.Failed($"{csproj} is not inside a clone under {DeployPaths.Repos} - cannot pull.");
            onLine($"→ git pull in {repoDir}");
            var pulled = await _ssh.ExecuteStreamAsync($"{DeployPaths.GitSshEnv} git -C {Shell.Quote(repoDir)} pull 2>&1", onLine);
            if (!pulled.Succeeded)
                throw OperationException.Failed("git pull failed - nothing was redeployed. See the output.", pulled.Text);
            onLine("");
        }

        onLine($"→ Publishing {csproj}");
        var published = await _ssh.ExecuteStreamAsync(
            $"{dotnetPath} publish {Shell.Quote(csproj)} -c Release -o {Shell.Quote(appDir)} 2>&1", onLine);
        if (!published.Succeeded)
            throw OperationException.Failed("dotnet publish failed - the service was NOT restarted. See the build output.", published.Text);

        await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(appDir)}");

        onLine($"\n→ Restarting {unit} (unit untouched)");
        var restarted = await _ssh.ExecuteAsync($"systemctl restart {unit} 2>&1");
        if (!restarted.Succeeded)
            throw OperationException.Failed($"Published, but {unit} failed to restart - check its journal. {restarted.Text}");
    }

    // "/srv/lrw/repos/shop/src/Shop.csproj" → "/srv/lrw/repos/shop"
    private static string? RepoDirOf(string csproj)
    {
        var prefix = DeployPaths.Repos + "/";
        if (!csproj.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var name = csproj[prefix.Length..].Split('/')[0];
        return RepositoryService.ValidateExistingName(name) == null ? DeployPaths.RepoDir(name) : null;
    }

    private async Task<string> FindDotnetAsync()
    {
        var path = (await _ssh.ExecuteAsync("command -v dotnet 2>/dev/null; true")).Output.Trim();
        return path.Length > 0
            ? path
            : throw OperationException.Failed(".NET SDK not found on the server. Install it first (.NET SDK panel, or `lrw dotnet install`).");
    }

    private static string BuildUnit(string app, string appDir, string dotnetPath, string dll, string urls) =>
        $"""
        [Unit]
        Description={app} (managed by LinuxRemoteWorker)
        After=network.target

        [Service]
        WorkingDirectory={appDir}
        ExecStart={dotnetPath} {appDir}/{dll}
        Restart=always
        RestartSec=5
        User={DeployPaths.ServiceUser}
        Environment=ASPNETCORE_ENVIRONMENT=Production
        Environment=ASPNETCORE_URLS={urls}

        [Install]
        WantedBy=multi-user.target
        """;

    // ── Running ────────────────────────────────────────────────────────────

    public async Task ControlAsync(string app, ServiceAction action)
    {
        OperationException.ThrowIfInvalid(ValidateAppName(app));
        var verb = action.ToString().ToLowerInvariant();
        var r = await _ssh.ExecuteAsync($"systemctl {verb} {DeployPaths.UnitName(app)} 2>&1");
        if (!r.Succeeded)
            throw OperationException.Failed($"{app}: {verb} failed - check its journal. {r.Text}");
    }

    /// <summary>Stops and disables the service and deletes its unit. The published app stays in /srv/lrw/apps.</summary>
    public async Task RemoveAsync(string app)
    {
        await EnsureExistsAsync(app);
        var unit = DeployPaths.UnitName(app);
        await _ssh.RunCommandAsync($"systemctl disable --now {unit} 2>/dev/null; rm -f {DeployPaths.UnitPath(app)}; systemctl daemon-reload");
    }

    /// <summary>Replaces the unit file, reloads systemd and restarts the service.</summary>
    public async Task SaveUnitAsync(string app, string content)
    {
        await EnsureExistsAsync(app);
        if (string.IsNullOrWhiteSpace(content)) throw OperationException.Invalid("The unit file cannot be empty.");

        await WriteUnitAsync(app, content);
        await _ssh.RunCommandAsync("systemctl daemon-reload");
        var r = await _ssh.ExecuteAsync($"systemctl restart {DeployPaths.UnitName(app)} 2>&1");
        if (!r.Succeeded)
            throw OperationException.Failed($"Unit saved, but the service failed to restart - check its journal. {r.Text}");
    }

    private async Task WriteUnitAsync(string app, string content)
    {
        // Quoted heredoc (no expansion) with LF line endings: a CR from the Windows editor would end up
        // inside ExecStart and systemd would not find the program
        var r = await _ssh.ExecuteAsync(Shell.WriteFile(DeployPaths.UnitPath(app), content));
        if (!r.Succeeded) throw OperationException.Failed($"Could not write {DeployPaths.UnitPath(app)}: {r.Text}");
    }

    // The unit file this tool writes, or any unit of that name systemd knows (e.g. one installed elsewhere)
    private async Task EnsureExistsAsync(string app)
    {
        OperationException.ThrowIfInvalid(ValidateAppName(app));
        var unit = DeployPaths.UnitName(app);
        var known = await _ssh.ExecuteAsync(
            $"[ -f {DeployPaths.UnitPath(app)} ] || systemctl list-unit-files {unit} --no-legend 2>/dev/null | grep -q .");
        if (!known.Succeeded)
            throw OperationException.NotFound($"No such service: {app} (no unit {unit}). See the list of services.");
    }
}
