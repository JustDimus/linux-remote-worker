using LinuxRemoteWorker.Cli.Commands;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Cli;

public class GitSourceOptionsTests
{
    [Fact]
    public void No_option_means_no_source()
    {
        Assert.Null(GitSourceOptions.From(null, null, null, false, null, false));
    }

    [Fact]
    public void Each_option_gives_its_source()
    {
        Assert.Equal(GitSource.Branch("develop"), GitSourceOptions.From("develop", null, null, false, null, false));
        Assert.Equal(GitSource.Tag("v1.4.2"), GitSourceOptions.From(null, "v1.4.2", null, false, null, false));
        Assert.Equal(GitSource.Commit("3f2a9c1"), GitSourceOptions.From(null, null, "3F2A9C1", false, null, false));
        Assert.Equal(GitSource.LatestRelease(), GitSourceOptions.From(null, null, null, true, null, false));
        Assert.Equal(GitSource.LatestRelease("api/v*", true), GitSourceOptions.From(null, null, null, true, "api/v*", true));
    }

    [Fact]
    public void Two_sources_at_once_are_invalid()
    {
        var ex = Assert.Throws<OperationException>(() => GitSourceOptions.From("main", "v1.0.0", null, false, null, false));

        Assert.Equal(FailureKind.Invalid, ex.Kind);
    }

    [Theory]
    [InlineData("v*", false)]
    [InlineData(null, true)]
    public void Pattern_and_prereleases_need_latest_release(string? pattern, bool prereleases)
    {
        var ex = Assert.Throws<OperationException>(() => GitSourceOptions.From("main", null, null, false, pattern, prereleases));

        Assert.Equal(FailureKind.Invalid, ex.Kind);
    }

    [Fact]
    public void An_unusable_name_is_invalid()
    {
        var ex = Assert.Throws<OperationException>(() => GitSourceOptions.From("--upload-pack=x", null, null, false, null, false));

        Assert.Equal(FailureKind.Invalid, ex.Kind);
    }
}

public class GitCommandLineTests
{
    private static readonly CommandCatalog Catalog = CommandCatalog.Build(typeof(RepositoryCommands).Assembly);

    private static ParsedCommandLine Parse(params string[] args) => CommandLineParser.Parse(Catalog, args);

    [Fact]
    public void Clone_takes_a_name_and_the_latest_release_options()
    {
        var parsed = Parse("repos", "clone", "--url", "git@github.com:acme/shop.git", "--name", "shop-dev",
            "--latest-release", "--pattern", "v*", "--prereleases");

        Assert.Equal("repos clone", parsed.Command?.FullPath);
        Assert.Equal("git@github.com:acme/shop.git", parsed.Values["url"]);
        Assert.Equal("shop-dev", parsed.Values["name"]);
        Assert.Equal(true, parsed.Values["latestRelease"]);
        Assert.Equal("v*", parsed.Values["pattern"]);
        Assert.Equal(true, parsed.Values["prereleases"]);
    }

    [Fact]
    public void Checkout_takes_the_clone_as_a_positional()
    {
        var parsed = Parse("repos", "checkout", "shop-prod", "--tag", "v1.4.2", "--force");

        Assert.Equal("repos checkout", parsed.Command?.FullPath);
        Assert.Equal("shop-prod", parsed.Values["name"]);
        Assert.Equal("v1.4.2", parsed.Values["tag"]);
        Assert.Equal(true, parsed.Values["force"]);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("pull")]
    public void Update_and_its_old_name_pull_take_force(string command)
    {
        var parsed = Parse("repos", command, "shop-dev", "--force");

        Assert.Equal($"repos {command}", parsed.Command?.FullPath);
        Assert.Equal("shop-dev", parsed.Values["name"]);
        Assert.Equal(true, parsed.Values["force"]);
    }

    [Fact]
    public void Refs_takes_a_url_or_a_clone()
    {
        Assert.Equal("shop-prod", Parse("repos", "refs", "shop-prod").Values["repository"]);
    }

    [Fact]
    public void Redeploy_can_update_first_or_move_to_a_version()
    {
        Assert.Equal(true, Parse("services", "redeploy", "shop-dev", "--update").Values["update"]);

        var rollback = Parse("services", "redeploy", "shop-prod", "--commit", "3f2a9c1", "--force");
        Assert.Equal("shop-prod", rollback.Values["app"]);
        Assert.Equal("3f2a9c1", rollback.Values["commit"]);
        Assert.Equal(true, rollback.Values["force"]);
    }

    [Fact]
    public void Deploy_takes_the_environment_and_can_skip_the_port_check()
    {
        var parsed = Parse("services", "deploy", "--repo", "shop-dev", "--name", "shop-dev",
            "--urls", "http://127.0.0.1:5001", "--environment", "Staging", "--skip-port-check");

        Assert.Equal("Staging", parsed.Values["environment"]);
        Assert.Equal(true, parsed.Values["skipPortCheck"]);
    }

    [Fact]
    public void A_mistyped_option_is_a_usage_error()
    {
        Assert.Throws<UsageException>(() => Parse("repos", "clone", "--url", "git@github.com:acme/shop.git", "--brnch", "main"));
    }
}
