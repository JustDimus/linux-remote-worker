using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Repositories;

public class GitSourceTests
{
    [Theory]
    [InlineData("branch:main")]
    [InlineData("branch:feature/login")]
    [InlineData("tag:v1.4.2")]
    [InlineData("commit:3f2a9c1")]
    [InlineData("latest-release:*")]
    [InlineData("latest-release:v*")]
    [InlineData("latest-release:api/v*:pre")]
    public void Serialize_round_trips_through_Parse(string text)
    {
        var source = GitSource.Parse(text);

        Assert.NotNull(source);
        Assert.Equal(text, source.Serialize());
    }

    [Fact]
    public void Parse_reads_the_prerelease_suffix()
    {
        var source = GitSource.Parse("latest-release:v*:pre");

        Assert.Equal(GitSource.LatestRelease("v*", includePrereleases: true), source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("main")]
    [InlineData(":main")]
    [InlineData("branch:")]
    [InlineData("branch:-rf")]
    [InlineData("branch:a..b")]
    [InlineData("tag:v1;reboot")]
    [InlineData("commit:xyz1234")]
    [InlineData("commit:3f2a")]
    [InlineData("latest-release:-v*")]
    [InlineData("release:v1")]
    public void Parse_returns_null_for_anything_unusable(string? text)
    {
        Assert.Null(GitSource.Parse(text));
    }

    [Fact]
    public void Commit_ids_are_stored_lower_case()
    {
        Assert.Equal("3f2a9c1", GitSource.Commit(" 3F2A9C1 ").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Latest_release_without_a_pattern_matches_any_tag(string? pattern)
    {
        Assert.Equal(GitSource.AnyTag, GitSource.LatestRelease(pattern).Value);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.x")]
    [InlineData("feature/JIRA-12_fix+docs")]
    public void Valid_branch_names_pass(string name)
    {
        Assert.Null(GitSource.Branch(name).Validate());
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("/x")]
    [InlineData(".x")]
    [InlineData("x/")]
    [InlineData("x.")]
    [InlineData("x.lock")]
    [InlineData("a//b")]
    [InlineData("a/.b")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("a$(id)")]
    [InlineData("a'b")]
    [InlineData("a~1")]
    public void Branch_names_git_or_a_shell_could_misread_are_rejected(string name)
    {
        Assert.NotNull(GitSource.Branch(name).Validate());
    }

    [Theory]
    [InlineData("abc12")]
    [InlineData("g123456")]
    [InlineData("3f2a9c1 --help")]
    public void Commits_must_be_7_or_more_hex_characters(string id)
    {
        Assert.NotNull(GitSource.Commit(id).Validate());
    }

    [Fact]
    public void A_sha256_commit_id_is_accepted()
    {
        Assert.Null(GitSource.Commit(new string('a', 64)).Validate());
    }

    [Theory]
    [InlineData("v*", true)]
    [InlineData("api/v*", true)]
    [InlineData("*", true)]
    [InlineData("-v*", false)]
    [InlineData("v$(id)", false)]
    [InlineData("v?", false)]
    public void Release_patterns_allow_only_a_star_as_wildcard(string pattern, bool valid)
    {
        Assert.Equal(valid, GitSource.LatestRelease(pattern).Validate() == null);
    }

    [Fact]
    public void Tags_and_commits_are_pinned_branches_and_releases_move()
    {
        Assert.True(GitSource.Tag("v1.0.0").IsPinned);
        Assert.True(GitSource.Commit("3f2a9c1").IsPinned);
        Assert.False(GitSource.Branch("main").IsPinned);
        Assert.False(GitSource.LatestRelease().IsPinned);
    }

    [Fact]
    public void Describe_reads_naturally()
    {
        Assert.Equal("branch main", GitSource.Branch("main").Describe());
        Assert.Equal("tag v1.4.2", GitSource.Tag("v1.4.2").Describe());
        Assert.Equal("commit 3f2a9c1", GitSource.Commit("3f2a9c1d4e5f60718293a4b5c6d7e8f901234567").Describe());
        Assert.Equal("latest release", GitSource.LatestRelease().Describe());
        Assert.Equal("latest release matching v*", GitSource.LatestRelease("v*").Describe());
        Assert.Equal("latest release (pre-releases included)", GitSource.LatestRelease(null, true).Describe());
    }
}
