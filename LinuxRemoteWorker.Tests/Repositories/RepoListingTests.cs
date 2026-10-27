using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Repositories;

public class RepoListingTests
{
    private const string Remote = "git@github.com:acme/shop.git";
    private static readonly string C1 = "8b1c0de" + new string('1', 33);
    private static readonly string C2 = "1234567" + new string('2', 33);
    private static readonly string C3 = "abcdef0" + new string('3', 33);

    // marker, then remote, lrw.track, branch, tag at HEAD, commit, last commit, first changed file, "end"
    private static string Record(string name, string remote, string track, string branch, string tag, string commit, string last, string changed) =>
        string.Join("\n", $"{RepoListing.Marker} {name}", remote, track, branch, tag, commit, last, changed, "end");

    [Fact]
    public void A_clone_following_the_latest_release()
    {
        var repo = Assert.Single(RepoListing.Parse(Record("shop-prod", Remote, "latest-release:v*", "", "v1.4.2", C1, "8b1c0de Release 1.4.2", "")));

        Assert.Equal("shop-prod", repo.Name);
        Assert.Equal(Remote, repo.Remote);
        Assert.Equal(GitSource.LatestRelease("v*"), repo.Source);
        Assert.Equal("v1.4.2", repo.Version);
        Assert.Equal(C1, repo.Commit);
        Assert.Equal("8b1c0de", repo.ShortCommit);
        Assert.Equal("8b1c0de Release 1.4.2", repo.LastCommit);
        Assert.False(repo.HasLocalChanges);
        Assert.Equal("latest release matching v*", repo.SourceText);
    }

    [Fact]
    public void A_clone_made_before_sources_were_recorded_follows_its_branch()
    {
        var repo = Assert.Single(RepoListing.Parse(Record("shop-dev", Remote, "", "develop", "", C2, "1234567 WIP", " M src/Program.cs")));

        Assert.Equal(GitSource.Branch("develop"), repo.Source);
        Assert.Equal("develop", repo.Version);
        Assert.True(repo.HasLocalChanges);
    }

    [Fact]
    public void A_detached_clone_without_a_record_follows_its_tag_or_commit()
    {
        var output = Record("at-tag", Remote, "", "", "v1.0.0", C1, "x", "") + "\n" +
                     Record("at-commit", Remote, "", "", "", C3, "y", "");

        var repos = RepoListing.Parse(output);

        Assert.Equal(GitSource.Tag("v1.0.0"), repos[0].Source);
        Assert.Equal("v1.0.0", repos[0].Version);
        Assert.Equal(GitSource.Commit(C3), repos[1].Source);
        Assert.Equal("abcdef0", repos[1].Version);
    }

    [Fact]
    public void A_folder_that_is_not_a_git_clone_shows_dashes()
    {
        var repo = Assert.Single(RepoListing.Parse(Record("junk", "", "", "", "", "", "", "")));

        Assert.Null(repo.Source);
        Assert.Equal("-", repo.Remote);
        Assert.Equal("-", repo.Version);
        Assert.Equal("-", repo.LastCommit);
        Assert.Equal("nothing checked out", repo.SourceText);
    }

    [Fact]
    public void A_recorded_source_wins_over_what_is_checked_out()
    {
        // pinned to a tag, but someone checked out a branch by hand
        var repo = Assert.Single(RepoListing.Parse(Record("shop", Remote, "tag:v1.0.0", "main", "", C1, "x", "")));

        Assert.Equal(GitSource.Tag("v1.0.0"), repo.Source);
        Assert.Equal("main", repo.Version);
    }

    [Fact]
    public void Skips_cut_off_records_and_noise_and_tolerates_CRLF()
    {
        var output = "bash: warning: something\r\n" +
                     Record("ok", Remote, "branch:main", "main", "", C1, "x", "").Replace("\n", "\r\n") + "\r\n" +
                     $"{RepoListing.Marker} cut\n{Remote}\nbranch:main\n";

        var repo = Assert.Single(RepoListing.Parse(output));

        Assert.Equal("ok", repo.Name);
        Assert.Equal(GitSource.Branch("main"), repo.Source);
    }

    [Fact]
    public void The_script_prints_the_marker_and_a_terminator_per_clone()
    {
        var script = RepoListing.Script("'/srv/lrw/repos/shop'");

        Assert.Contains("for d in '/srv/lrw/repos/shop'; do", script);
        Assert.Contains(RepoListing.Marker, script);
        Assert.Contains("config --get lrw.track", script);
        Assert.Contains(" end", script);
    }
}
