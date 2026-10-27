using System.Text.RegularExpressions;

namespace LinuxRemoteWorker.Modules.Repositories;

public enum GitSourceKind { Branch, Tag, Commit, LatestRelease }

/// <summary>
/// What a clone follows. A branch and the latest release move forward when the clone is updated; a tag
/// or a commit is a fixed version. The clone keeps it in its own git config as lrw.track, for example
/// "branch:main", "tag:v1.4.2", "commit:3f2a9c1", "latest-release:*" or "latest-release:v*:pre".
/// </summary>
public sealed record GitSource(GitSourceKind Kind, string Value, bool IncludePrereleases = false)
{
    /// <summary>The tag pattern that matches every tag.</summary>
    public const string AnyTag = "*";

    // A subset of git check-ref-format: also no characters a shell, URL or config file could reinterpret
    private static readonly Regex RefName = new(@"^[A-Za-z0-9._/+-]{1,200}$");
    private static readonly Regex CommitId = new(@"^[0-9a-f]{7,64}$");
    private static readonly Regex TagPattern = new(@"^[A-Za-z0-9._/+*-]{1,100}$");

    public static GitSource Branch(string name) => new(GitSourceKind.Branch, name.Trim());

    public static GitSource Tag(string name) => new(GitSourceKind.Tag, name.Trim());

    public static GitSource Commit(string id) => new(GitSourceKind.Commit, id.Trim().ToLowerInvariant());

    public static GitSource LatestRelease(string? pattern = null, bool includePrereleases = false) =>
        new(GitSourceKind.LatestRelease, string.IsNullOrWhiteSpace(pattern) ? AnyTag : pattern.Trim(), includePrereleases);

    /// <summary>A tag or a commit: updating the clone never moves it.</summary>
    public bool IsPinned => Kind is GitSourceKind.Tag or GitSourceKind.Commit;

    /// <summary>"branch main", "tag v1.4.2", "commit 3f2a9c1", "latest release matching v*".</summary>
    public string Describe() => Kind switch
    {
        GitSourceKind.Branch => $"branch {Value}",
        GitSourceKind.Tag => $"tag {Value}",
        GitSourceKind.Commit => $"commit {GitRefs.Short(Value)}",
        _ => "latest release" + (Value == AnyTag ? "" : $" matching {Value}") + (IncludePrereleases ? " (pre-releases included)" : "")
    };

    /// <summary>The lrw.track form: "branch:main", "latest-release:v*:pre" …</summary>
    public string Serialize() => Kind switch
    {
        GitSourceKind.Branch => $"branch:{Value}",
        GitSourceKind.Tag => $"tag:{Value}",
        GitSourceKind.Commit => $"commit:{Value}",
        _ => $"latest-release:{Value}" + (IncludePrereleases ? ":pre" : "")
    };

    /// <summary>Reads the lrw.track form; null when it is empty or not valid.</summary>
    public static GitSource? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var colon = value.IndexOf(':');
        if (colon <= 0) return null;

        var rest = value[(colon + 1)..];
        var source = value[..colon] switch
        {
            "branch" => Branch(rest),
            "tag" => Tag(rest),
            "commit" => Commit(rest),
            "latest-release" when rest.EndsWith(":pre", StringComparison.Ordinal) => LatestRelease(rest[..^4], includePrereleases: true),
            "latest-release" => LatestRelease(rest),
            _ => null
        };
        return source?.Validate() == null ? source : null;
    }

    /// <summary>The error message for an unusable value, or null.</summary>
    public string? Validate() => Kind switch
    {
        GitSourceKind.Branch => ValidateRefName(Value, "Branch"),
        GitSourceKind.Tag => ValidateRefName(Value, "Tag"),
        GitSourceKind.Commit => CommitId.IsMatch(Value) ? null : $"Commit '{Value}': 7 to 64 hexadecimal characters of a commit hash.",
        _ => TagPattern.IsMatch(Value) && !Value.StartsWith('-')
            ? null
            : $"Release tag pattern '{Value}': letters, digits and . _ / + - plus * as a wildcard, for example v*."
    };

    /// <summary>Branch and tag names that git accepts and that need no escaping anywhere.</summary>
    public static string? ValidateRefName(string name, string what) =>
        RefName.IsMatch(name)
        && name[0] is not ('-' or '/' or '.')
        && name[^1] is not ('/' or '.')
        && !name.EndsWith(".lock", StringComparison.Ordinal)
        && !name.Contains("..") && !name.Contains("//") && !name.Contains("/.")
            ? null
            : $"{what} '{name}' is not a valid git name.";
}
