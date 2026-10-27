using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("repos", "Git clones under /srv/lrw/repos: each follows a branch, a tag, a commit or the latest release")]
public sealed class RepositoryCommands : CommandSet
{
    public RepositoryCommands(CommandContext context) : base(context) { }

    private async Task<RepositoryService> ReposAsync() => new(await SshAsync());

    [Command("status", "git version (null when missing), the deploy key's public half (null until generated), and every clone with what it follows and what it has checked out.",
        ReadOnly = true)]
    public async Task<RepoStatus> Status() => await (await ReposAsync()).GetStatusAsync();

    [Command("list", "Clones: remote, what each follows (source), the checked-out version and commit, and whether files were changed on the server.",
        ReadOnly = true)]
    public async Task<List<RepoInfo>> List() => await (await ReposAsync()).ListAsync();

    [Command("refs", "Branches, tags (newest version first) and the latest release of a repository URL or of a clone's origin - to choose what to clone or check out.",
        ReadOnly = true, Example = "repos refs git@github.com:acme/shop.git")]
    public async Task<RemoteRefs> Refs([Arg("Repository URL, or the folder name of a clone", Positional = true)] string repository) =>
        await (await ReposAsync()).ListRefsAsync(repository);

    [Command("install-git", "Install git (apt).", Streams = true, Idempotent = true)]
    public async Task<Outcome> InstallGit()
    {
        var repos = await ReposAsync();
        await repos.InstallGitAsync(Lines);
        return new Outcome("git installed") { Data = new { gitVersion = await repos.GetGitVersionAsync() } };
    }

    [Command("deploy-key", "Show the server's SSH deploy key (public half) to add to GitHub/GitLab as a read-only deploy key. --generate creates it when missing; an existing key is never replaced.",
        Idempotent = true)]
    public async Task<object> DeployKey([Arg("Create the key if the server has none yet")] bool generate = false)
    {
        var repos = await ReposAsync();
        var key = generate ? await repos.GenerateDeployKeyAsync() : await repos.GetDeployKeyAsync();
        if (key == null)
            throw OperationException.NotFound("The server has no deploy key yet - run `lrw repos deploy-key --generate`.");

        return new
        {
            publicKey = key,
            privateKeyPath = DeployPaths.GitKey,
            next = "Add publicKey to the repository's Deploy keys (read access is enough), then clone with `lrw repos clone`."
        };
    }

    [Command("clone", "Clone a repository with the deploy key into /srv/lrw/repos/<name> at a branch (default: the default branch), a tag, a commit or the latest release. " +
                      "Clone the same repository again under another --name to run a second instance at another version.",
        Streams = true, Example = "repos clone --url git@github.com:acme/shop.git --name shop-prod --latest-release")]
    public async Task<Outcome> Clone(
        [Arg("https://…, ssh://… or git@host:owner/repo.git")] string url,
        [Arg("Folder name under /srv/lrw/repos (default: the repository name)")] string? name = null,
        [Arg(GitSourceOptions.BranchHelp)] string? branch = null,
        [Arg(GitSourceOptions.TagHelp)] string? tag = null,
        [Arg(GitSourceOptions.CommitHelp)] string? commit = null,
        [Arg(GitSourceOptions.LatestReleaseHelp)] bool latestRelease = false,
        [Arg(GitSourceOptions.PatternHelp)] string? pattern = null,
        [Arg(GitSourceOptions.PrereleasesHelp)] bool prereleases = false)
    {
        var source = GitSourceOptions.From(branch, tag, commit, latestRelease, pattern, prereleases);
        var change = await (await ReposAsync()).CloneAsync(url, source, name, Lines);
        return new Outcome(change.Summary) { Data = change };
    }

    [Command("checkout", "Move a clone to another version or make it follow something else: a branch, a tag, a commit or the latest release. " +
                         "Refused when tracked files were changed on the server, unless --force. Redeploy the services built from it afterwards.",
        Example = "repos checkout shop-prod --tag v1.5.0")]
    public async Task<Outcome> Checkout(
        [Arg("Folder name under /srv/lrw/repos", Positional = true)] string name,
        [Arg(GitSourceOptions.BranchHelp)] string? branch = null,
        [Arg(GitSourceOptions.TagHelp)] string? tag = null,
        [Arg(GitSourceOptions.CommitHelp)] string? commit = null,
        [Arg(GitSourceOptions.LatestReleaseHelp)] bool latestRelease = false,
        [Arg(GitSourceOptions.PatternHelp)] string? pattern = null,
        [Arg(GitSourceOptions.PrereleasesHelp)] bool prereleases = false,
        [Arg("Discard files changed on the server and reset a diverged branch to origin")] bool force = false)
    {
        var source = GitSourceOptions.From(branch, tag, commit, latestRelease, pattern, prereleases)
                     ?? throw OperationException.Invalid("Choose what to check out: --branch, --tag, --commit or --latest-release.");
        var change = await (await ReposAsync()).SwitchAsync(name, source, force, Lines);
        return new Outcome(change.Summary) { Data = change };
    }

    [Command("update", "Bring a clone up to date with what it follows: the newest commit of its branch (fast-forward only) or the newest release. A clone pinned to a tag or commit stays put.",
        Idempotent = true, Example = "repos update shop-dev")]
    public async Task<Outcome> Update(
        [Arg("Folder name under /srv/lrw/repos", Positional = true)] string name,
        [Arg("Discard files changed on the server and reset a diverged branch to origin")] bool force = false)
    {
        var change = await (await ReposAsync()).UpdateAsync(name, force, Lines);
        return new Outcome(change.Summary) { Data = change };
    }

    [Command("pull", "Same as `repos update` (kept for scripts written before it existed).", Idempotent = true)]
    public async Task<Outcome> Pull(
        [Arg("Folder name under /srv/lrw/repos", Positional = true)] string name,
        [Arg("Discard files changed on the server and reset a diverged branch to origin")] bool force = false) =>
        await Update(name, force);

    [Command("remove", "Delete a clone. Uncommitted changes on the server are lost.", Destructive = true)]
    public async Task<Outcome> Remove([Arg("Folder name under /srv/lrw/repos", Positional = true)] string name)
    {
        await (await ReposAsync()).DeleteAsync(name);
        return new Outcome($"Removed {DeployPaths.RepoDir(name)}");
    }
}
