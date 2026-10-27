using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Cli.Commands;

/// <summary>Turns --branch / --tag / --commit / --latest-release [--pattern] [--prereleases] into one <see cref="GitSource"/>.</summary>
internal static class GitSourceOptions
{
    public const string BranchHelp = "Follow this branch: updates bring its newest commit";
    public const string TagHelp = "Check out this tag and stay on it";
    public const string CommitHelp = "Check out this commit (its hash, at least 7 hex characters) and stay on it";
    public const string LatestReleaseHelp = "Check out the newest release tag (semver, e.g. v1.4.2); updates move to newer releases";
    public const string PatternHelp = "With --latest-release: only tags matching this pattern, * as wildcard (e.g. v*, api/v*)";
    public const string PrereleasesHelp = "With --latest-release: also consider pre-releases such as v2.0.0-rc.1";

    /// <returns>The chosen source, or null when none of the options was given.</returns>
    public static GitSource? From(string? branch, string? tag, string? commit, bool latestRelease, string? pattern, bool prereleases)
    {
        var chosen = new[] { branch != null, tag != null, commit != null, latestRelease }.Count(given => given);
        if (chosen > 1)
            throw OperationException.Invalid("Choose one of --branch, --tag, --commit or --latest-release.");
        if ((pattern != null || prereleases) && !latestRelease)
            throw OperationException.Invalid("--pattern and --prereleases go together with --latest-release.");

        var source = branch != null ? GitSource.Branch(branch)
            : tag != null ? GitSource.Tag(tag)
            : commit != null ? GitSource.Commit(commit)
            : latestRelease ? GitSource.LatestRelease(pattern, prereleases)
            : null;
        OperationException.ThrowIfInvalid(source?.Validate());
        return source;
    }
}
