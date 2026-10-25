using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("repos", "Git clones under /srv/lrw/repos, fetched with the server's deploy key")]
public sealed class RepositoryCommands : CommandSet
{
    public RepositoryCommands(CommandContext context) : base(context) { }

    private async Task<RepositoryService> ReposAsync() => new(await SshAsync());

    [Command("status", "git version (null when missing), the deploy key's public half (null until generated), and every clone with its remote, branch and last commit.",
        ReadOnly = true)]
    public async Task<RepoStatus> Status() => await (await ReposAsync()).GetStatusAsync();

    [Command("list", "Clones with remote, branch and last commit.", ReadOnly = true)]
    public async Task<List<RepoInfo>> List() => await (await ReposAsync()).ListAsync();

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

    [Command("clone", "Clone a repository with the deploy key into /srv/lrw/repos/<name> (owned by the lrw service user).",
        Streams = true, Example = "repos clone --url git@github.com:acme/shop.git --branch main")]
    public async Task<Outcome> Clone(
        [Arg("https://…, ssh://… or git@host:owner/repo.git")] string url,
        [Arg("Branch to check out (default: the remote's default branch)")] string? branch = null,
        [Arg("Folder name under /srv/lrw/repos (default: the repository name)")] string? name = null)
    {
        var dir = await (await ReposAsync()).CloneAsync(url, branch, name, Lines);
        return new Outcome($"Cloned into {dir}") { Data = new { name = dir.Split('/').Last(), dir } };
    }

    [Command("pull", "git pull a clone with the deploy key.")]
    public async Task<Outcome> Pull([Arg("Folder name under /srv/lrw/repos", Positional = true)] string name)
    {
        var output = await (await ReposAsync()).PullAsync(name);
        return new Outcome($"{name}: pulled") { Output = output };
    }

    [Command("remove", "Delete a clone. Uncommitted changes on the server are lost.", Destructive = true)]
    public async Task<Outcome> Remove([Arg("Folder name under /srv/lrw/repos", Positional = true)] string name)
    {
        await (await ReposAsync()).DeleteAsync(name);
        return new Outcome($"Removed {DeployPaths.RepoDir(name)}");
    }
}
