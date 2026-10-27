using System.Globalization;
using System.Text.RegularExpressions;

namespace LinuxRemoteWorker.Modules.Repositories;

/// <summary>A branch or tag and the commit it points to (for an annotated tag: the tagged commit).</summary>
public sealed record GitRef(string Name, string Commit);

/// <summary>Branches and tags read from `git ls-remote` (a remote) or `git show-ref -d` (a clone).</summary>
public sealed record GitRefList(string? DefaultBranch, IReadOnlyList<GitRef> Branches, IReadOnlyList<GitRef> Tags)
{
    public GitRef? Branch(string name) => Branches.FirstOrDefault(b => b.Name == name);

    public GitRef? Tag(string name) => Tags.FirstOrDefault(t => t.Name == name);
}

public static class GitRefs
{
    /// <summary>The first 7 characters of a commit id, as git shows it.</summary>
    public static string Short(string? commit) => string.IsNullOrEmpty(commit) ? "-" : commit.Length <= 7 ? commit : commit[..7];

    /// <summary>
    /// Parses
    /// <code>
    /// ref: refs/heads/main	HEAD           (ls-remote --symref: the default branch)
    /// 3f2a…	refs/heads/main
    /// 8b1c…	refs/remotes/origin/develop    (show-ref in a clone: remote branches)
    /// 9d4e…	refs/tags/v1.4.2
    /// 3f2a…	refs/tags/v1.4.2^{}            (the commit an annotated tag points to)
    /// </code>
    /// Fields may be separated by tabs (ls-remote) or spaces (show-ref).
    /// </summary>
    public static GitRefList Parse(string output)
    {
        string? defaultBranch = null;
        var branches = new List<GitRef>();
        var tags = new List<GitRef>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var fields = line["ref: refs/heads/".Length..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && fields[1] == "HEAD") defaultBranch = fields[0];
                continue;
            }

            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !IsCommitId(parts[0])) continue;
            var (commit, refName) = (parts[0], parts[1]);

            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                Upsert(branches, refName["refs/heads/".Length..], commit);
            else if (refName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && refName != "refs/remotes/origin/HEAD")
                Upsert(branches, refName["refs/remotes/origin/".Length..], commit);
            else if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                var name = refName["refs/tags/".Length..];
                // the peeled line ("^{}") always follows its tag and carries the commit
                if (name.EndsWith("^{}", StringComparison.Ordinal)) Upsert(tags, name[..^3], commit);
                else Upsert(tags, name, commit);
            }
        }

        return new GitRefList(defaultBranch, branches, tags);
    }

    private static void Upsert(List<GitRef> refs, string name, string commit)
    {
        var index = refs.FindIndex(r => r.Name == name);
        if (index >= 0) refs[index] = new GitRef(name, commit);
        else refs.Add(new GitRef(name, commit));
    }

    private static bool IsCommitId(string value) => value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);
}

/// <summary>
/// The version in a release tag: v1.4.2, 1.4, 1.4.2.7, 2.0.0-rc.1, api/v1.4.0 (a prefix up to the last / is
/// ignored, for monorepos). Ordered like semver: numbers first, then a release above its pre-releases.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(@"^[vV]?(?<nums>\d+(\.\d+){1,3})(-(?<pre>[0-9A-Za-z.-]+))?(\+[0-9A-Za-z.-]+)?$");

    private ReleaseVersion(int[] numbers, string? prerelease)
    {
        Numbers = numbers;
        Prerelease = prerelease;
    }

    public IReadOnlyList<int> Numbers { get; }

    /// <summary>"rc.1" in 2.0.0-rc.1; null for a release.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => Prerelease != null;

    public static ReleaseVersion? TryParse(string tag)
    {
        var name = tag[(tag.LastIndexOf('/') + 1)..];
        var m = Pattern.Match(name);
        if (!m.Success) return null;

        var parts = m.Groups["nums"].Value.Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;

        return new ReleaseVersion(numbers, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = Part(this, i).CompareTo(Part(other, i));
            if (c != 0) return c;
        }

        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease == null) return 1;
        if (other.Prerelease == null) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int Part(ReleaseVersion v, int index) => index < v.Numbers.Count ? v.Numbers[index] : 0;

    // semver 11.4: dot-separated identifiers; numeric ones compare as numbers and rank below alphanumeric ones
    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
            var c = (leftNumeric, rightNumeric) switch
            {
                (true, true) => l.CompareTo(r),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };
            if (c != 0) return c;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => string.Join('.', Numbers) + (Prerelease == null ? "" : "-" + Prerelease);
}

public static class ReleaseSelector
{
    /// <summary>The tag with the highest version among those matching <paramref name="pattern"/> (* = any text).</summary>
    public static GitRef? Latest(IEnumerable<GitRef> tags, string pattern, bool includePrereleases) =>
        Versioned(tags, pattern)
            .Where(x => includePrereleases || !x.Version.IsPrerelease)
            .Select(x => x.Tag)
            .FirstOrDefault();

    /// <summary>Tags with a version, highest first, then every other tag by name.</summary>
    public static List<GitRef> SortNewestFirst(IEnumerable<GitRef> tags)
    {
        var all = tags.ToList();
        var versioned = Versioned(all, GitSource.AnyTag).Select(x => x.Tag).ToList();
        return versioned.Concat(all.Except(versioned).OrderBy(t => t.Name, StringComparer.Ordinal)).ToList();
    }

    public static bool Matches(string tag, string pattern) =>
        pattern == GitSource.AnyTag || Regex.IsMatch(tag, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$");

    private static IEnumerable<(GitRef Tag, ReleaseVersion Version)> Versioned(IEnumerable<GitRef> tags, string pattern) =>
        tags.Where(t => Matches(t.Name, pattern))
            .Select(t => (Tag: t, Version: ReleaseVersion.TryParse(t.Name)))
            .Where(x => x.Version != null)
            .Select(x => (x.Tag, Version: x.Version!))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Tag.Name, StringComparer.Ordinal);
}
