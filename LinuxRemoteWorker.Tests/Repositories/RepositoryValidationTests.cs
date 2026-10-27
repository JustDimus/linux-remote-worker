using LinuxRemoteWorker.Modules.Repositories;
using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Tests.Repositories;

public class RepositoryValidationTests
{
    [Theory]
    [InlineData("git@github.com:acme/shop.git", "shop")]
    [InlineData("https://github.com/acme/shop", "shop")]
    [InlineData("https://github.com/acme/shop.git/", "shop")]
    [InlineData("ssh://git@git.example.com:2222/acme/shop.git", "shop")]
    [InlineData("git@github.com:shop.git", "shop")]
    public void The_default_folder_is_the_repository_name(string url, string expected)
    {
        Assert.Equal(expected, RepositoryService.DefaultFolderName(url));
    }

    [Theory]
    [InlineData("git@github.com:acme/shop.git", true)]
    [InlineData("https://github.com/acme/shop.git", true)]
    [InlineData("ssh://git@host/acme/shop.git", true)]
    [InlineData("git://host/shop.git", true)]
    [InlineData("shop", false)]
    [InlineData("shop-prod", false)]
    [InlineData("ftp://host/shop.git", false)]
    [InlineData("-oProxyCommand=id", false)]
    [InlineData("git@github.com:acme/shop.git; reboot", false)]
    [InlineData("https://github.com/acme/shop.git reboot", false)]
    [InlineData("", false)]
    public void Repository_urls_are_told_apart_from_clone_names(string text, bool expected)
    {
        Assert.Equal(expected, RepositoryService.IsRepositoryUrl(text));
    }

    [Theory]
    [InlineData("shop", true)]
    [InlineData("shop-prod", true)]
    [InlineData("shop_2", true)]
    [InlineData("Shop.Api", true)]
    [InlineData("", false)]
    [InlineData("-shop", false)]
    [InlineData(".shop", false)]
    [InlineData("../shop", false)]
    [InlineData("a/b", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    public void New_folder_names_are_strict(string name, bool valid)
    {
        Assert.Equal(valid, RepositoryService.ValidateName(name) == null);
    }

    [Fact]
    public void New_folder_names_are_at_most_100_characters()
    {
        Assert.Null(RepositoryService.ValidateName(new string('a', 100)));
        Assert.NotNull(RepositoryService.ValidateName(new string('a', 101)));
    }

    [Theory]
    [InlineData("Shop Old", true)]
    [InlineData("shop", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\nb", false)]
    public void Existing_clone_names_only_must_stay_inside_the_repos_folder(string name, bool valid)
    {
        Assert.Equal(valid, RepositoryService.ValidateExistingName(name) == null);
    }

    [Fact]
    public void A_clone_request_reports_the_first_problem()
    {
        const string url = "git@github.com:acme/shop.git";

        Assert.Contains("repository URL", RepositoryService.ValidateClone("shop", "shop", null));
        Assert.Contains("Folder name", RepositoryService.ValidateClone(url, "a/b", null));
        Assert.Contains("not a valid git name", RepositoryService.ValidateClone(url, "shop", GitSource.Branch("-x")));
        Assert.Null(RepositoryService.ValidateClone($"  {url}  ", "shop-dev", GitSource.LatestRelease("v*")));
        Assert.Null(RepositoryService.ValidateClone(url, "shop", null));
    }

    [Theory]
    [InlineData("/srv/lrw/repos/shop/src/Shop.Api/Shop.Api.csproj", "shop")]
    [InlineData("/srv/lrw/repos/shop-dev/Shop.csproj", "shop-dev")]
    [InlineData("/home/deploy/Shop.csproj", null)]
    [InlineData("/srv/lrw/repos/../etc/x.csproj", null)]
    [InlineData("/srv/lrw/reposx/shop/Shop.csproj", null)]
    public void A_project_knows_the_clone_it_belongs_to(string csproj, string? expected)
    {
        Assert.Equal(expected, AppServiceManager.RepoNameOf(csproj));
    }
}
