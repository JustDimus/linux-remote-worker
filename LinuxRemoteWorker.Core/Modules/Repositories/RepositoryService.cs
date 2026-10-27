using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Repositories;

/// <summary>git installed (version text, null when missing), the deploy key's public half (null until generated), and the clones.</summary>
public sealed record RepoStatus(string? GitVersion, string? DeployPublicKey, IReadOnlyList<RepoInfo> Repos);

/// <summary>
/// Branches and tags of a repository: tags newest version first, the newest release, and the newest
/// pre-release when it is newer than that release.
/// </summary>
public sealed record RemoteRefs(
    string Repository,
    string? DefaultBranch,
    IReadOnlyList<GitRef> Branches,
    IReadOnlyList<GitRef> Tags,
    GitRef? LatestRelease,
    GitRef? LatestPrerelease);

/// <summary>What a clone, checkout or update did to a clone.</summary>
public sealed record GitChange(
    string Name,
    string Directory,
    GitSource Source,
    string? FromVersion,
    string? FromCommit,
    string Version,
    string Commit,
    bool Changed)
{
    public string Summary =>
        FromVersion == null ? $"{Name}: cloned {Version} ({GitRefs.Short(Commit)}), following {Source.Describe()}"
        : Changed ? $"{Name}: {FromVersion} ({GitRefs.Short(FromCommit)}) → {Version} ({GitRefs.Short(Commit)}), following {Source.Describe()}"
        : Source.IsPinned ? $"{Name} is pinned to {Source.Describe()} - nothing to update"
        : $"{Name} is up to date: {Version} ({GitRefs.Short(Commit)}), following {Source.Describe()}";
}

/// <summary>
/// Git clones under /srv/lrw/repos, fetched with the server-side deploy key so they never depend on
/// the root user's own SSH configuration. Each clone follows a <see cref="GitSource"/> - a branch, a tag,
/// a commit or the latest release - so one repository can be cloned several times at different versions.
/// </summary>
public sealed class RepositoryService
{
    // https://host/owner/repo(.git), ssh://…, or scp-style git@host:owner/repo.git
    private static readonly Regex RepoUrl = new(@"^((https?|ssh|git)://\S+|[\w.-]+@[\w.-]+:[\w./~-]+)$");
    private static readonly Regex FolderName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

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

    public async Task InstallGitAsync(Action<string> onLine)
    {
        var r = await _ssh.ExecuteStreamAsync(Shell.AptInstall("git"), onLine);
        if (!r.Succeeded)
            throw OperationException.Failed($"Installing git failed (exit {r.ExitCode}) - see the output.", r.Text);
    }

    // ── Validation ─────────────────────────────────────────────────────────

    /// <summary>The folder a clone of <paramref name="url"/> gets when no name is given: the last path part without .git.</summary>
    public static string DefaultFolderName(string url) =>
        Regex.Replace(url.Trim().TrimEnd('/').Split('/', ':').Last(), @"\.git$", "");

    public static bool IsRepositoryUrl(string text) => RepoUrl.IsMatch(text.Trim());

    public static string? ValidateUrl(string url) =>
        IsRepositoryUrl(url)
            ? null
            : "Enter a repository URL such as https://github.com/owner/repo.git or git@github.com:owner/repo.git";

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
    public static string? ValidateClone(string url, string name, GitSource? source) =>
        ValidateUrl(url.Trim()) ?? ValidateName(name) ?? source?.Validate();

    // ── Reading ────────────────────────────────────────────────────────────

    public async Task<List<RepoInfo>> ListAsync()
    {
        var r = await _ssh.ExecuteAsync(RepoListing.Script(DeployPaths.Repos + "/*/"));
        return RepoListing.Parse(r.Output);
    }

    public async Task<RepoInfo> GetAsync(string name)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(name));
        var dir = Shell.Quote(DeployPaths.RepoDir(name));
        var r = await _ssh.ExecuteAsync($"[ -d {dir} ] || exit {GitScripts.ExitNotFound}\n" + RepoListing.Script(dir));
        if (r.ExitCode == GitScripts.ExitNotFound)
            throw OperationException.NotFound($"No such repository: {name} (see the list of clones).");
        return RepoListing.Parse(r.Output).FirstOrDefault()
               ?? throw OperationException.Failed($"Could not read the clone {name}.", r.Text);
    }

    /// <summary>Branches, tags and releases of a repository URL, or of the origin of a clone (by its folder name).</summary>
    public async Task<RemoteRefs> ListRefsAsync(string urlOrClone)
    {
        var target = urlOrClone.Trim();
        await _bootstrap.EnsureAsync();

        string command;
        if (IsRepositoryUrl(target))
        {
            command = GitScripts.LsRemote(target);
        }
        else
        {
            await GetAsync(target);
            command = GitScripts.LsRemoteOfClone(DeployPaths.RepoDir(target));
        }

        var r = await _ssh.ExecuteAsync(command);
        if (!r.Succeeded)
            throw OperationException.Failed(
                $"Could not read the branches and tags of {target} - is the deploy key added to the repository?", r.Text);

        var refs = GitRefs.Parse(r.Output);
        var newest = ReleaseSelector.Latest(refs.Tags, GitSource.AnyTag, includePrereleases: true);
        return new RemoteRefs(
            target,
            refs.DefaultBranch,
            refs.Branches.OrderBy(b => b.Name, StringComparer.Ordinal).ToList(),
            ReleaseSelector.SortNewestFirst(refs.Tags),
            ReleaseSelector.Latest(refs.Tags, GitSource.AnyTag, includePrereleases: false),
            newest != null && ReleaseVersion.TryParse(newest.Name)!.IsPrerelease ? newest : null);
    }

    // ── Changing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Clones into /srv/lrw/repos/&lt;name&gt; at <paramref name="source"/> (the default branch when null) and
    /// remembers it. The same repository can be cloned again under another name, at another version.
    /// </summary>
    /// <param name="name">Folder name; derived from the URL when empty.</param>
    public async Task<GitChange> CloneAsync(string url, GitSource? source, string? name, Action<string> onLine)
    {
        url = url.Trim();
        name = string.IsNullOrWhiteSpace(name) ? DefaultFolderName(url) : name.Trim();
        OperationException.ThrowIfInvalid(ValidateClone(url, name, source));

        await _bootstrap.EnsureAsync();
        var dir = DeployPaths.RepoDir(name);
        if ((await _ssh.ExecuteAsync($"[ -e {Shell.Quote(dir)} ]")).Succeeded)
            throw OperationException.Invalid($"{dir} already exists - pick another folder name, or update or remove that clone.");

        onLine($"→ Reading branches and tags of {url}");
        var listed = await _ssh.ExecuteAsync(GitScripts.LsRemote(url));
        if (!listed.Succeeded)
            throw OperationException.Failed(
                "The repository could not be read - is the URL right and the deploy key added to the repository?", listed.Text);
        var refs = GitRefs.Parse(listed.Output);

        var follow = source
                     ?? (refs.DefaultBranch != null
                         ? GitSource.Branch(refs.DefaultBranch)
                         : throw OperationException.Invalid($"{url} has no default branch - choose a branch, tag or commit."));
        var target = GitScripts.Resolve(follow, refs, url);

        onLine($"→ Cloning {url} → {dir} at {target.Describe()}");
        var r = await _ssh.ExecuteStreamAsync(GitScripts.Clone(url, dir, target, follow), onLine);
        if (r.ExitCode == GitScripts.ExitMissingRef) throw OperationException.NotFound(r.Text);
        if (!r.Succeeded)
            throw OperationException.Failed("git clone failed - see the output (is the deploy key added to the repository?).", r.Text);

        await ChownAsync(dir);
        var after = await GetAsync(name);
        return new GitChange(name, dir, follow, null, null, after.Version, after.Commit, true);
    }

    /// <summary>
    /// Moves a clone to another version or makes it follow something else: a branch, a tag, a commit or the
    /// latest release. Refused when tracked files were changed on the server, unless <paramref name="force"/>.
    /// </summary>
    public async Task<GitChange> SwitchAsync(string name, GitSource source, bool force, Action<string> onLine)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(name) ?? source.Validate());
        await _bootstrap.EnsureAsync();
        return await MoveAsync(await GetAsync(name), source, force, onLine);
    }

    /// <summary>
    /// Brings a clone up to date with what it follows: the newest commit of its branch, or the newest release.
    /// A clone pinned to a tag or a commit stays where it is.
    /// </summary>
    public async Task<GitChange> UpdateAsync(string name, bool force, Action<string> onLine)
    {
        OperationException.ThrowIfInvalid(ValidateExistingName(name));
        await _bootstrap.EnsureAsync();

        var before = await GetAsync(name);
        var source = before.Source
                     ?? throw OperationException.Failed($"{name} has nothing checked out - choose a version to check out first.");
        if (source.IsPinned)
            return new GitChange(name, DeployPaths.RepoDir(name), source, before.Version, before.Commit, before.Version, before.Commit, false);

        return await MoveAsync(before, source, force, onLine);
    }

    private async Task<GitChange> MoveAsync(RepoInfo before, GitSource source, bool force, Action<string> onLine)
    {
        var dir = DeployPaths.RepoDir(before.Name);

        onLine($"→ Fetching {before.Remote}");
        var fetched = await _ssh.ExecuteAsync(GitScripts.FetchRefs(dir, force));
        if (fetched.ExitCode == GitScripts.ExitNotFound) throw OperationException.NotFound($"No such repository: {before.Name}.");
        if (fetched.ExitCode == GitScripts.ExitLocalChanges)
            throw new OperationException(FailureKind.Refused,
                $"{before.Name} has changed files on the server. Commit or discard them there, or use force to discard them.", fetched.Text);
        if (!fetched.Succeeded)
            throw OperationException.Failed("git fetch failed - is the deploy key still added to the repository?", fetched.Text);

        var marker = fetched.Output.IndexOf(GitScripts.RefsMarker, StringComparison.Ordinal);
        foreach (var line in (marker >= 0 ? fetched.Output[..marker] : fetched.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            onLine(line.TrimEnd('\r'));
        var refs = GitRefs.Parse(marker >= 0 ? fetched.Output[(marker + GitScripts.RefsMarker.Length)..] : "");

        var target = GitScripts.Resolve(source, refs, $"the origin of {before.Name}");
        onLine($"→ Checking out {target.Describe()}");
        var r = await _ssh.ExecuteAsync(GitScripts.Checkout(dir, target, source, force));
        if (r.ExitCode == GitScripts.ExitMissingRef) throw OperationException.NotFound(r.Text);
        if (r.ExitCode == GitScripts.ExitDiverged)
            throw new OperationException(FailureKind.Refused,
                $"Branch {target.Ref} of {before.Name} has diverged from origin (rewritten history or local commits). " +
                "Nothing was changed; use force to reset the clone to origin.", r.Text);
        if (!r.Succeeded) throw OperationException.Failed($"git checkout of {target.Describe()} failed.", r.Text);

        await ChownAsync(dir);
        var after = await GetAsync(before.Name);
        var changed = after.Commit != before.Commit || before.Source != source;
        return new GitChange(before.Name, dir, source, before.Version, before.Commit, after.Version, after.Commit, changed);
    }

    // Checkouts run as root; the files belong to the service user that builds and runs them
    private async Task ChownAsync(string dir) =>
        await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(dir)}");

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
