using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Repositories;

/// <summary>A resolved checkout target: a branch name, a tag name or a commit id (a release resolves to its tag).</summary>
internal sealed record GitTarget(GitSourceKind Kind, string Ref)
{
    public string Describe() => Kind switch
    {
        GitSourceKind.Branch => $"branch {Ref}",
        GitSourceKind.Tag => $"tag {Ref}",
        _ => $"commit {GitRefs.Short(Ref)}"
    };
}

/// <summary>
/// The shell scripts behind clone, checkout and update. Every name in them was validated by
/// <see cref="GitSource.Validate"/> and is shell-quoted; refs are passed as full ref names, and "--"
/// ends the options, so nothing a user types can become a git option or a path.
/// </summary>
internal static class GitScripts
{
    public const int ExitNotFound = 3;
    public const int ExitLocalChanges = 5;
    public const int ExitDiverged = 6;
    public const int ExitMissingRef = 7;

    /// <summary>Separates the fetch output from the ref listing in <see cref="FetchRefs"/>.</summary>
    public const string RefsMarker = "@@LRW-REFS@@";

    private static string Q(string value) => Shell.Quote(value);

    /// <summary>Default branch, branches and tags of a repository URL, without cloning it.</summary>
    public static string LsRemote(string url) =>
        $"{DeployPaths.GitSshEnv} git ls-remote --symref {Q(url)} HEAD 'refs/heads/*' 'refs/tags/*' 2>&1";

    /// <summary>The same for the origin of an existing clone.</summary>
    public static string LsRemoteOfClone(string dir) =>
        $"{DeployPaths.GitSshEnv} git -C {Q(dir)} ls-remote --symref origin HEAD 'refs/heads/*' 'refs/tags/*' 2>&1";

    /// <summary>A full clone checked out at <paramref name="target"/>, remembering what it follows; nothing is left behind on failure.</summary>
    public static string Clone(string url, string dir, GitTarget target, GitSource track)
    {
        var d = Q(dir);
        var cleanup = $"{{ rm -rf -- {d}; exit 1; }}";
        var lines = new List<string>();
        if (target.Kind == GitSourceKind.Commit)
        {
            lines.Add($"{DeployPaths.GitSshEnv} git clone --no-checkout -- {Q(url)} {d} 2>&1 || exit 1");
            lines.Add($"git -C {d} cat-file -e {Q(target.Ref + "^{commit}")} 2>/dev/null || " +
                      $"{{ echo {Q($"Commit {target.Ref} is not in the repository.")}; rm -rf -- {d}; exit {ExitMissingRef}; }}");
            lines.Add($"git -C {d} -c advice.detachedHead=false checkout -q --detach {Q(target.Ref)} -- 2>&1 || {cleanup}");
        }
        else
        {
            // --branch takes a branch or a tag; a tag leaves HEAD detached at it
            lines.Add($"{DeployPaths.GitSshEnv} git -c advice.detachedHead=false clone --branch {Q(target.Ref)} -- {Q(url)} {d} 2>&1 || exit 1");
        }
        lines.Add($"git -C {d} config lrw.track {Q(track.Serialize())} || {cleanup}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// First half of checkout and update: refuse when tracked files were changed on the server (unless
    /// <paramref name="force"/>), fetch branches and tags, then list every ref after <see cref="RefsMarker"/>.
    /// </summary>
    public static string FetchRefs(string dir, bool force)
    {
        var lines = new List<string> { $"cd {Q(dir)} || exit {ExitNotFound}" };
        if (!force)
            lines.Add("if [ -n \"$(git status --porcelain --untracked-files=no)\" ]; then " +
                      $"echo 'Changed files:'; git status --short --untracked-files=no; exit {ExitLocalChanges}; fi");
        lines.Add($"{DeployPaths.GitSshEnv} git fetch --prune --tags --force origin 2>&1 || exit 1");
        lines.Add($"echo '{RefsMarker}'");
        lines.Add("git show-ref -d");
        lines.Add("true");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Second half: check out the target and remember what the clone follows. A branch moves only by
    /// fast-forward; when it diverged (rewritten history, local commits) only <paramref name="force"/>
    /// resets it to origin. Tags and commits are checked out detached.
    /// </summary>
    public static string Checkout(string dir, GitTarget target, GitSource track, bool force)
    {
        var f = force ? " -f" : "";
        var lines = new List<string> { $"cd {Q(dir)} || exit {ExitNotFound}" };

        switch (target.Kind)
        {
            case GitSourceKind.Branch:
                var branch = Q(target.Ref);
                var remote = Q("refs/remotes/origin/" + target.Ref);
                lines.Add($"git rev-parse --verify -q {remote} >/dev/null || " +
                          $"{{ echo {Q($"origin has no branch {target.Ref}.")}; exit {ExitMissingRef}; }}");
                lines.Add($"if git show-ref --verify -q {Q("refs/heads/" + target.Ref)}; then");
                lines.Add($"  git checkout -q{f} {branch} -- 2>&1 || exit 1");
                if (force)
                {
                    lines.Add($"  git reset -q --hard {remote} 2>&1 || exit 1");
                }
                else
                {
                    lines.Add($"  git merge-base --is-ancestor HEAD {remote} || " +
                              $"{{ echo {Q($"Branch {target.Ref} has diverged from origin (rewritten history or local commits).")}; exit {ExitDiverged}; }}");
                    lines.Add($"  git merge -q --ff-only {remote} 2>&1 || exit 1");
                }
                lines.Add("else");
                lines.Add($"  git checkout -q{f} --track -b {branch} {remote} 2>&1 || exit 1");
                lines.Add("fi");
                break;

            case GitSourceKind.Tag:
                var tag = "refs/tags/" + target.Ref;
                lines.Add($"git rev-parse --verify -q {Q(tag + "^{commit}")} >/dev/null || " +
                          $"{{ echo {Q($"There is no tag {target.Ref}.")}; exit {ExitMissingRef}; }}");
                lines.Add($"git -c advice.detachedHead=false checkout -q{f} --detach {Q(tag)} -- 2>&1 || exit 1");
                break;

            default:
                lines.Add($"git cat-file -e {Q(target.Ref + "^{commit}")} 2>/dev/null || " +
                          $"{{ echo {Q($"Commit {target.Ref} is not in the repository.")}; exit {ExitMissingRef}; }}");
                lines.Add($"git -c advice.detachedHead=false checkout -q{f} --detach {Q(target.Ref)} -- 2>&1 || exit 1");
                break;
        }

        lines.Add($"git config lrw.track {Q(track.Serialize())}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// What <paramref name="source"/> means in <paramref name="refs"/>: the branch, the tag, the newest matching
    /// release, or the commit as given (a commit can only be checked once it is fetched).
    /// </summary>
    public static GitTarget Resolve(GitSource source, GitRefList refs, string where)
    {
        switch (source.Kind)
        {
            case GitSourceKind.Branch:
                return refs.Branch(source.Value) != null
                    ? new GitTarget(GitSourceKind.Branch, source.Value)
                    : throw OperationException.NotFound($"{where} has no branch '{source.Value}'. Branches: {List(refs.Branches.Select(b => b.Name))}.");

            case GitSourceKind.Tag:
                return refs.Tag(source.Value) != null
                    ? new GitTarget(GitSourceKind.Tag, source.Value)
                    : throw OperationException.NotFound($"{where} has no tag '{source.Value}'. Newest tags: {List(ReleaseSelector.SortNewestFirst(refs.Tags).Select(t => t.Name))}.");

            case GitSourceKind.LatestRelease:
                var release = ReleaseSelector.Latest(refs.Tags, source.Value, source.IncludePrereleases);
                return release != null
                    ? new GitTarget(GitSourceKind.Tag, release.Name)
                    : throw OperationException.NotFound(
                        $"No release tag in {where} matches '{source.Value}'" +
                        (source.IncludePrereleases ? "." : " (pre-releases such as v2.0.0-rc.1 are left out unless asked for).") +
                        $" Tags: {List(ReleaseSelector.SortNewestFirst(refs.Tags).Select(t => t.Name))}.");

            default:
                return new GitTarget(GitSourceKind.Commit, source.Value);
        }
    }

    private static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count == 0 ? "none" : string.Join(", ", all.Take(10)) + (all.Count > 10 ? $", … ({all.Count} in total)" : "");
    }
}
