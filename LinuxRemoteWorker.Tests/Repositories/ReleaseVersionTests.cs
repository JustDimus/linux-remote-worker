using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Repositories;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.4.2", "1.4.2")]
    [InlineData("V1.4.2", "1.4.2")]
    [InlineData("1.4", "1.4")]
    [InlineData("1.4.2.7", "1.4.2.7")]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1")]
    [InlineData("v3.0.0+build.5", "3.0.0")]
    [InlineData("api/v1.4.0", "1.4.0")]
    [InlineData("services/api/2.1.0-beta", "2.1.0-beta")]
    public void Release_tags_are_read(string tag, string expected)
    {
        Assert.Equal(expected, ReleaseVersion.TryParse(tag)?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("latest")]
    [InlineData("nightly-2026-10-26")]
    [InlineData("release-1.0")]
    [InlineData("1.x")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("v99999999999.0.0")]
    public void Other_tags_are_not_versions(string tag)
    {
        Assert.Null(ReleaseVersion.TryParse(tag));
    }

    [Fact]
    public void Pre_releases_are_flagged()
    {
        Assert.True(ReleaseVersion.TryParse("v2.0.0-rc.1")!.IsPrerelease);
        Assert.False(ReleaseVersion.TryParse("v2.0.0")!.IsPrerelease);
    }

    [Fact]
    public void Numbers_compare_numerically()
    {
        Assert.True(V("1.10.0").CompareTo(V("1.9.0")) > 0);
        Assert.True(V("2.0").CompareTo(V("1.99.99")) > 0);
    }

    [Fact]
    public void Missing_parts_count_as_zero()
    {
        Assert.Equal(0, V("1.4").CompareTo(V("1.4.0")));
        Assert.True(V("1.4.0.1").CompareTo(V("1.4")) > 0);
    }

    [Fact]
    public void Ordering_follows_semver_precedence()
    {
        // The example list from semver.org 11.4, lowest first
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
        ];

        for (var i = 1; i < ordered.Length; i++)
            Assert.True(V(ordered[i]).CompareTo(V(ordered[i - 1])) > 0, $"{ordered[i]} should rank above {ordered[i - 1]}");
    }

    private static ReleaseVersion V(string tag) => ReleaseVersion.TryParse(tag)!;
}

public class ReleaseSelectorTests
{
    private static readonly GitRef[] Tags =
    [
        new("v1.2.0", "1111111"),
        new("v1.10.0", "2222222"),
        new("v1.9.3", "3333333"),
        new("v2.0.0-rc.1", "4444444"),
        new("nightly", "5555555"),
        new("api/v3.0.0", "6666666"),
        new("latest", "7777777")
    ];

    [Fact]
    public void Latest_release_is_the_highest_version_not_the_newest_name()
    {
        Assert.Equal("v1.10.0", ReleaseSelector.Latest(Tags, "v*", includePrereleases: false)?.Name);
    }

    [Fact]
    public void Pre_releases_count_only_when_asked_for()
    {
        Assert.Equal("v2.0.0-rc.1", ReleaseSelector.Latest(Tags, "v*", includePrereleases: true)?.Name);
    }

    [Fact]
    public void A_pattern_selects_one_component_of_a_monorepo()
    {
        Assert.Equal("api/v3.0.0", ReleaseSelector.Latest(Tags, "api/v*", includePrereleases: false)?.Name);
    }

    [Fact]
    public void The_any_pattern_considers_every_versioned_tag()
    {
        Assert.Equal("api/v3.0.0", ReleaseSelector.Latest(Tags, GitSource.AnyTag, includePrereleases: false)?.Name);
    }

    [Fact]
    public void No_versioned_tag_means_no_release()
    {
        Assert.Null(ReleaseSelector.Latest([new GitRef("nightly", "5555555")], GitSource.AnyTag, includePrereleases: true));
        Assert.Null(ReleaseSelector.Latest(Tags, "web/v*", includePrereleases: true));
    }

    [Fact]
    public void Sorting_puts_versions_first_highest_first_then_other_tags_by_name()
    {
        var names = ReleaseSelector.SortNewestFirst(Tags).Select(t => t.Name);

        Assert.Equal(["api/v3.0.0", "v2.0.0-rc.1", "v1.10.0", "v1.9.3", "v1.2.0", "latest", "nightly"], names);
    }

    [Fact]
    public void Equal_versions_are_ordered_by_name_so_the_choice_is_stable()
    {
        GitRef[] tags = [new("v1.4", "1111111"), new("v1.4.0", "2222222")];

        Assert.Equal("v1.4.0", ReleaseSelector.Latest(tags, GitSource.AnyTag, false)?.Name);
    }

    [Theory]
    [InlineData("v1.2.3", "v*", true)]
    [InlineData("release-1", "v*", false)]
    [InlineData("api/v1.0", "api/v*", true)]
    [InlineData("api/v1.0", "v*", false)]
    [InlineData("v1.0", "v1.0", true)]
    [InlineData("v1x0", "v1.0", false)]
    [InlineData("anything", "*", true)]
    public void Patterns_match_whole_tag_names(string tag, string pattern, bool expected)
    {
        Assert.Equal(expected, ReleaseSelector.Matches(tag, pattern));
    }
}
