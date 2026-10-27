namespace LinuxRemoteWorker.Modules.Repositories;

/// <summary>
/// A clone under /srv/lrw/repos. <see cref="Version"/> is what is checked out: the branch, else a tag at
/// HEAD, else the short commit. <see cref="Source"/> is what an update follows; clones made before it was
/// recorded follow what they have checked out.
/// </summary>
public sealed record RepoInfo(
    string Name,
    string Remote,
    GitSource? Source,
    string Version,
    string Commit,
    string LastCommit,
    bool HasLocalChanges)
{
    public string SourceText => Source?.Describe() ?? "nothing checked out";

    public string ShortCommit => GitRefs.Short(Commit);
}

/// <summary>Reads every clone (or one) with a single command instead of several per clone.</summary>
internal static class RepoListing
{
    public const string Marker = "@@LRW-REPO@@";

    // remote, lrw.track, branch, tag at HEAD, commit, last commit line, first changed file, then "end"
    private const int Fields = 8;

    /// <param name="dirs">Shell words naming clone directories: the /srv/lrw/repos/*/ glob, or one quoted directory.</param>
    public static string Script(string dirs) => string.Join("\n",
        $"for d in {dirs}; do",
        "  [ -d \"$d\" ] || continue",
        $"  printf '%s %s\\n' '{Marker}' \"$(basename \"$d\")\"",
        "  printf '%s\\n' \"$(git -C \"$d\" remote get-url origin 2>/dev/null)\" \"$(git -C \"$d\" config --get lrw.track 2>/dev/null)\" \\",
        "    \"$(git -C \"$d\" symbolic-ref -q --short HEAD 2>/dev/null)\" \"$(git -C \"$d\" describe --tags --exact-match HEAD 2>/dev/null)\" \\",
        "    \"$(git -C \"$d\" rev-parse HEAD 2>/dev/null)\" \"$(git -C \"$d\" log -1 --format='%h %s' 2>/dev/null)\" \\",
        "    \"$(git -C \"$d\" status --porcelain --untracked-files=no 2>/dev/null | head -n 1)\" end",
        "done",
        "true");

    public static List<RepoInfo> Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var repos = new List<RepoInfo>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(Marker + " ", StringComparison.Ordinal)) continue;
            if (i + Fields >= lines.Length || lines[i + Fields] != "end") continue;

            var at = i;
            string Field(int n) => lines[at + n].Trim();
            var (remote, track, branch, tag, commit, last, changed) = (Field(1), Field(2), Field(3), Field(4), Field(5), Field(6), Field(7));

            var source = GitSource.Parse(track)
                         ?? (branch.Length > 0 ? GitSource.Branch(branch)
                             : tag.Length > 0 ? GitSource.Tag(tag)
                             : commit.Length > 0 ? GitSource.Commit(commit)
                             : null);
            var version = commit.Length == 0 ? "-" : branch.Length > 0 ? branch : tag.Length > 0 ? tag : GitRefs.Short(commit);

            repos.Add(new RepoInfo(lines[i][(Marker.Length + 1)..].Trim(), remote.Length > 0 ? remote : "-", source, version,
                commit, last.Length > 0 ? last : "-", changed.Length > 0));
            i += Fields;
        }
        return repos;
    }
}
