using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;
using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("services", ".NET apps deployed as systemd units lrw-<app>.service, running as the lrw user")]
public sealed class ServiceCommands : CommandSet
{
    public ServiceCommands(CommandContext context) : base(context) { }

    private async Task<AppServiceManager> ServicesAsync() => new(await SshAsync());

    [Command("list", "Every deployed service with its unit name and whether it is active and enabled.", ReadOnly = true)]
    public async Task<List<ServiceInfo>> List() => await (await ServicesAsync()).ListAsync();

    [Command("show", "One service: state, the .csproj it is published from, its paths, and `systemctl status` with the last journal lines.",
        ReadOnly = true)]
    public async Task<object> Show([Arg("App name", Positional = true)] string app)
    {
        var services = await ServicesAsync();
        var info = await services.GetAsync(app);
        return new
        {
            info.AppName,
            info.UnitName,
            info.IsActive,
            info.IsEnabled,
            sourceProject = await services.GetSourceProjectAsync(app),
            appDir = DeployPaths.AppDir(app),
            unitPath = DeployPaths.UnitPath(app),
            logDir = DeployPaths.LogDir(app),
            status = await services.GetStatusTextAsync(app)
        };
    }

    [Command("projects", "The .csproj files that can be deployed: of one clone, or of every clone.", ReadOnly = true)]
    public async Task<object> Projects([Arg("Clone name under /srv/lrw/repos (default: all)")] string? repo = null)
    {
        var services = await ServicesAsync();
        List<string> repos = repo != null
            ? [repo]
            : (await services.ListReposAsync()).Where(r => RepositoryService.ValidateExistingName(r) == null).ToList();
        var result = new List<object>();
        foreach (var r in repos)
            foreach (var project in await services.FindProjectsAsync(r))
                result.Add(new { repo = r, project, suggestedName = AppServiceManager.SuggestAppName(project) });
        return result;
    }

    [Command("deploy", "Publish a project (dotnet publish -c Release) to /srv/lrw/apps/<name>, write its systemd unit with ASPNETCORE_URLS, and enable + start it. " +
                       "Give --csproj (absolute, or relative to --repo), or only --repo when it holds a single project. Re-deploying replaces the unit.",
        Streams = true, Example = "services deploy --repo shop --csproj src/Shop.Api/Shop.Api.csproj --name shop-api --urls http://127.0.0.1:5000")]
    public async Task<Outcome> Deploy(
        [Arg("Clone name under /srv/lrw/repos")] string? repo = null,
        [Arg(".csproj path: absolute, or relative to the clone")] string? csproj = null,
        [Arg("App name: lower-case letters, digits, . - _ (default: from the .csproj name)")] string? name = null,
        [Arg("ASPNETCORE_URLS; use http://127.0.0.1:<port> when nginx proxies to it")] string urls = "http://0.0.0.0:5000")
    {
        var services = await ServicesAsync();
        var project = await ResolveProjectAsync(services, repo, csproj);
        var app = string.IsNullOrWhiteSpace(name) ? AppServiceManager.SuggestAppName(project) : name.Trim();

        await services.DeployAsync(project, app, urls, Lines);
        return new Outcome($"Service {app} deployed and started")
        {
            Data = await services.GetAsync(app),
            Warning = urls.Contains("0.0.0.0") || urls.Contains("[::]") || urls.Contains("*")
                ? "The app listens on every interface. Behind nginx, prefer http://127.0.0.1:<port> so it is not reachable directly."
                : null
        };
    }

    [Command("redeploy", "Re-publish a service from the .csproj it was deployed from and restart it (the unit is untouched). --pull runs git pull first.",
        Streams = true, Example = "services redeploy shop-api --pull")]
    public async Task<Outcome> Redeploy(
        [Arg("App name", Positional = true)] string app,
        [Arg("git pull the clone before publishing")] bool pull = false)
    {
        var services = await ServicesAsync();
        await services.RedeployAsync(app, pull, Lines);
        return new Outcome($"{app} redeployed" + (pull ? " from the latest commit" : "")) { Data = await services.GetAsync(app) };
    }

    [Command("start", "Start a service.", Idempotent = true)]
    public async Task<Outcome> Start([Arg("App name", Positional = true)] string app) => await ControlAsync(app, ServiceAction.Start);

    [Command("stop", "Stop a service (it starts again at boot while enabled).", Idempotent = true)]
    public async Task<Outcome> Stop([Arg("App name", Positional = true)] string app) => await ControlAsync(app, ServiceAction.Stop);

    [Command("restart", "Restart a service.")]
    public async Task<Outcome> Restart([Arg("App name", Positional = true)] string app) => await ControlAsync(app, ServiceAction.Restart);

    [Command("remove", "Stop and disable a service and delete its unit. The published files stay in /srv/lrw/apps/<app>.", Destructive = true)]
    public async Task<Outcome> Remove([Arg("App name", Positional = true)] string app)
    {
        await (await ServicesAsync()).RemoveAsync(app);
        return new Outcome($"Service {app} removed (app files kept in {DeployPaths.AppDir(app)})");
    }

    [Command("unit show", "The systemd unit file of a service.", ReadOnly = true)]
    public async Task<object> UnitShow([Arg("App name", Positional = true)] string app) =>
        new { path = DeployPaths.UnitPath(app), content = await (await ServicesAsync()).ReadUnitAsync(app) };

    [Command("unit set", "Replace the unit file of a service, reload systemd and restart it (e.g. to add Environment= lines). Read it first with unit show.",
        Unrestricted = true, Example = "services unit set shop-api --content @shop-api.service")]
    public async Task<Outcome> UnitSet(
        [Arg("App name", Positional = true)] string app,
        [Arg("The complete unit file", AllowFile = true)] string content)
    {
        var services = await ServicesAsync();
        await services.SaveUnitAsync(app, content);
        return new Outcome("Unit saved, systemd reloaded, service restarted") { Data = await services.GetAsync(app) };
    }

    private async Task<Outcome> ControlAsync(string app, ServiceAction action)
    {
        var services = await ServicesAsync();
        await services.ControlAsync(app, action);
        return new Outcome($"{app}: {action.ToString().ToLowerInvariant()} done") { Data = await services.GetAsync(app) };
    }

    private static async Task<string> ResolveProjectAsync(AppServiceManager services, string? repo, string? csproj)
    {
        if (!string.IsNullOrWhiteSpace(csproj))
        {
            csproj = csproj.Trim().Replace('\\', '/');
            if (csproj.StartsWith('/')) return csproj;
            if (string.IsNullOrWhiteSpace(repo))
                throw OperationException.Invalid("A relative --csproj needs --repo (or pass the absolute path under /srv/lrw/repos).");
            return $"{DeployPaths.RepoDir(repo.Trim())}/{(csproj.StartsWith("./") ? csproj[2..] : csproj)}";
        }

        if (string.IsNullOrWhiteSpace(repo))
            throw OperationException.Invalid("Pass --csproj, or --repo when the clone holds a single project (see `lrw services projects`).");

        var projects = await services.FindProjectsAsync(repo.Trim());
        return projects.Count switch
        {
            0 => throw OperationException.NotFound($"No .csproj found in {DeployPaths.RepoDir(repo)}."),
            1 => projects[0],
            _ => throw OperationException.Invalid(
                $"{repo} holds several projects - pass --csproj with one of: {string.Join(", ", projects)}")
        };
    }
}
