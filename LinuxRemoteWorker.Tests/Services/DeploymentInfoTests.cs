using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Tests.Services;

public class DeploymentInfoTests
{
    private static readonly DeploymentInfo Deployment = new(
        "/srv/lrw/repos/shop-prod/src/Shop.Api/Shop.Api.csproj",
        "shop-prod",
        "latest-release:v*",
        "v1.4.2",
        "8b1c0de" + new string('1', 33),
        "Production",
        "http://127.0.0.1:5000",
        new DateTime(2026, 10, 26, 14, 5, 0, DateTimeKind.Utc));

    [Fact]
    public void Survives_a_round_trip_through_its_file()
    {
        Assert.Equal(Deployment, DeploymentInfo.FromJson(Deployment.ToJson()));
    }

    [Fact]
    public void The_file_uses_camel_case_names()
    {
        var json = Deployment.ToJson();

        Assert.Contains("\"deployedAtUtc\"", json);
        Assert.Contains("\"repository\": \"shop-prod\"", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void A_missing_or_broken_file_means_no_record(string? json)
    {
        Assert.Null(DeploymentInfo.FromJson(json));
    }

    [Fact]
    public void Describe_names_version_commit_clone_environment_and_time()
    {
        Assert.Equal("v1.4.2 (8b1c0de) from shop-prod · Production · 2026-10-26 14:05 UTC", Deployment.Describe());
    }

    [Fact]
    public void A_project_outside_a_clone_has_no_version()
    {
        var plain = Deployment with { Repository = null, Source = null, Version = null, Commit = null, Environment = "Staging" };

        Assert.Equal("unknown version · Staging · 2026-10-26 14:05 UTC", plain.Describe());
    }

    [Fact]
    public void A_service_shows_what_it_runs()
    {
        Assert.Equal(Deployment.Describe(), new ServiceInfo("shop-prod", "lrw-shop-prod.service", true, true, Deployment).VersionText);
        Assert.Equal("", new ServiceInfo("old", "lrw-old.service", true, true).VersionText);
    }
}
