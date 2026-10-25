using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Repositories;

/// <summary>git installed (version text, null when missing), the deploy key's public half (null until generated), and the clones.</summary>
public sealed record RepoStatus(string? GitVersion, string? DeployPublicKey, IReadOnlyList<RepoInfo> Repos);

/// <summary>
/// Git clones under /srv/lrw/repos, fetched with the server-side deploy key so they never depend on
/// the root user's own SSH configuration.
/// </summary>
public sealed class RepositoryService
{
    // https://host/owner/repo(.git), ssh://…, or scp-style git@host:owner/repo.git
    private static readonly Regex RepoUrl = new(@"^((https?|ssh|git)://\S+|[\w.-]+@[\w.-]+:[\w./~-]+)$");
    private static readonly Regex FolderName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");
    private static readonly Regex BranchName = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$");

    private readonly SshService _ssh;
    private readonly BootstrapService _bootstrap;

    public RepositoryService(SshService ssh)
    {
        _ssh = ssh;
        _bootstrap = new BootstrapService(ssh);
    }

    public async Task<RepoStatus> GetStatusAsync()
    {
        await _bootstrap.EnsureAsync();
        return new RepoStatus(await GetGitVersionAsync(), await GetDeployKeyAsync(), await ListAsync());
    }

    public async Task<string?> GetGitVersionAsync()
    {
        var git = await _ssh.RunCommandAsync("git --version 2>/dev/null || echo ''");
        return string.IsNullOrWhiteSpace(git) ? null : git.Trim();
    }

    public async Task<string?> GetDeployKeyAsync()
    {
        var pub = await _ssh.RunCommandAsync(
            $"[ -f {DeployPaths.GitKeyPub} ] && cat {DeployPaths.GitKeyPub} || echo ''");
        return string.IsNullOrWhiteSpace(pub) ? null : pub.Trim();
    }

    /// <summary>Creates the ed25519 deploy key unless it exists, and returns its public half.</summary>
    public async Task<string> GenerateDeployKeyAsync()
    {
        await _bootstrap.EnsureAsync();
        // Don't overwrite an existing key
        await _ssh.RunCommandAsync(
            $"[ -f {DeployPaths.GitKey} ] || ssh-keygen -t ed25519 -f {DeployPaths.GitKey} -N '' -C 'lrw-deploy'");
        await _ssh.RunCommandAsync(
            $"chown {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {DeployPaths.GitKey} {DeployPaths.GitKeyPub} && chmod 600 {DeployPaths.GitKey}");

        return await GetDeployKeyAsync()
               ?? throw OperationException.Failed("The deploy key could not be generated - is ssh-keygen installed?");
    }

    public async Task<List<RepoInfo>> ListAsync()
    {
        var repos = new List<RepoInfo>();
        var names = await _ssh.RunCommandAsync($"ls -1 {DeployPaths.Repos} 2>/dev/null");
        foreach (var name in names.Split('\n').Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var dir = Shell.Quote(DeployPaths.RepoDir(name.Trim()));
            var remote = await _ssh.RunCommandAsync($"git -C {dir} remote get-url origin 2>/dev/null || echo '-'");
            var branch = await _ssh.RunCommandAsync($"git -C {dir} branch --show-current 2>/dev/null || echo '-'");
            var commit = await _ssh.RunCommandAsync($"git -C {dir} log -1 --format='%h %s' 2>/dev/null || echo '-'");
            repos.Add(new RepoInfo(name.Trim(), remote.Trim(), branch.Trim(), commit.Trim()));
        }
        return repos;
    }

    public async Task InstallGitAsync(Action<string> onLine)
    {
        var r = await _ssh.ExecuteStreamAsync(Shell.AptInstall("git"), onLine);
        if (!r.Succeeded)
            throw OperationException.Failed($"Installing git failed (exit {r.ExitCode}) - see the output.", r.Text);
    }

    /// <summary>The folder a clone of <paramref name="url"/> gets when no name is given: the last path part without .git.</summary>
    public static string DefaultFolderName(string url) =>
        Regex.Replace(url.Trim().TrimEnd('/').Split('/', ':').Last(), @"\.git$", "");

    /// <summary>Folder names become paths that are deleted as root: no slashes, no "..".</summary>
    public static string? ValidateName(string name) =>
        FolderName.IsMatch(name) && !name.Contains("..") ? null : "Folder name: letters, digits, '.', '-' and '_' only.";

    /// <summary>
    /// For clones that already exist (one made by hand may break the strict rule): only what would
    /// leave /srv/lrw/repos is rejected - the name is shell-quoted wherever it is used.
    /// </summary>
    public static string? ValidateExistingName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0 && !name.Any(char.IsControl)
            ? null
            : $"'{name}' is not a folder name inside {DeployPaths.Repos}.";

    /// <summary>Validates a clone request; returns the error message, or null when it can go ahead.</summary>
    public static string? ValidateClone(string url, string branch, string name) =>
        !RepoUrl.IsMatch(url)
            ? "Enter a repository URL such as https://github.com/owner/repo.git or git@github.com:owner/repo.git"
            : branch.Length > 0 && (!BranchName.IsMatch(branch) || branch.Contains(".."))
                ? "That branch name is not valid."
                : ValidateName(name);

    /// <summary>Clones with the deploy key into /srv/lrw/repos/&lt;name&gt; and returns that directory.</summary>
    /// <param name="name">Folder name; derived from the URL when empty.</param>
    public async Task<string> CloneAsync(string url, string? branch, string? name, Action<string> onLine)
    {
        url = url.Trim();
        branch = branch?.Trim() ?? string.Empty;
        name = string.IsNullOrWhiteSpace(name) ? DefaultFolderName(url) : name.Trim();
        OperationException.ThrowIfInvalid(ValidateClone(url, branch, name));

        await _bootstrap.EnsureAsync();

        var dir = DeployPaths.RepoDir(name);
        var branchPart = branch.Length == 0 ? "" : $"--branch {Shell.Quote(branch)} ";

        onLine($"→ Cloning {url} → {dir}");

        // Clone using deploy key; "--" so nothing in the URL can be read as an option
        var r = await _ssh.ExecuteStreamAsync(
            $"{DeployPaths.GitSshEnv} git clone {branchPart}-- {Shell.Quote(url)} {Shell.Quote(dir)} 2>&1", onLine);
        if (!r.Succeeded)
            throw OperationException.Failed(
                "git clone failed - see the output (is the deploy key added to the repository?).", r.Text);

        // Hand ownership to service user
        await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(dir)}");
        return dir;
    }

    /// <summary>git pull with the deploy key; returns git's output.</summary>
    public async Task<string> PullAsync(string name)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(name));

        var dir = Shell.Quote(DeployPaths.RepoDir(name));
        var missing = Shell.Quote($"No such repository: {name}");
        // .git is a file, not a directory, in worktrees and submodules
        var r = await _ssh.ExecuteAsync(
            $"[ -e {dir}/.git ] || {{ echo {missing}; exit 3; }}; {DeployPaths.GitSshEnv} git -C {dir} pull 2>&1");
        if (r.ExitCode == 3) throw OperationException.NotFound(r.Text);
        if (!r.Succeeded) throw OperationException.Failed($"Pull failed: {r.Text}");
        return r.Text;
    }

    /// <summary>Deletes the clone. Uncommitted changes on the server are lost.</summary>
    public async Task DeleteAsync(string name)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(name));

        var dir = Shell.Quote(DeployPaths.RepoDir(name));
        var missing = Shell.Quote($"No such repository: {name}");
        var r = await _ssh.ExecuteAsync($"[ -d {dir} ] || {{ echo {missing}; exit 3; }}; rm -rf -- {dir} 2>&1");
        if (r.ExitCode == 3) throw OperationException.NotFound(r.Text);
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
    }
}
