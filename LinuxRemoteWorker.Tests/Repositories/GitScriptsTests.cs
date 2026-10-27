using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Repositories;

public class GitScriptsTests
{
    private const string Url = "git@github.com:acme/shop.git";
    private const string Dir = "/srv/lrw/repos/shop";

    private static readonly GitRefList Refs = new(
        "main",
        [new GitRef("main", new string('1', 40)), new GitRef("develop", new string('2', 40))],
        [
            new GitRef("v1.0.0", new string('3', 40)),
            new GitRef("v1.1.0", new string('4', 40)),
            new GitRef("v1.2.0-rc.1", new string('5', 40))
        ]);

    // ── Resolve ────────────────────────────────────────────────────────────

    [Fact]
    public void Resolves_a_branch_that_exists()
    {
        Assert.Equal(new GitTarget(GitSourceKind.Branch, "develop"), GitScripts.Resolve(GitSource.Branch("develop"), Refs, Url));
    }

    [Fact]
    public void A_missing_branch_is_not_found_and_lists_the_branches()
    {
        var ex = Assert.Throws<OperationException>(() => GitScripts.Resolve(GitSource.Branch("release"), Refs, Url));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Contains("Branches: main, develop", ex.Message);
    }

    [Fact]
    public void A_missing_tag_is_not_found_and_lists_the_newest_tags()
    {
        var ex = Assert.Throws<OperationException>(() => GitScripts.Resolve(GitSource.Tag("v9.9.9"), Refs, Url));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Contains("Newest tags: v1.2.0-rc.1, v1.1.0, v1.0.0", ex.Message);
    }

    [Fact]
    public void The_latest_release_resolves_to_its_tag()
    {
        Assert.Equal(new GitTarget(GitSourceKind.Tag, "v1.1.0"), GitScripts.Resolve(GitSource.LatestRelease(), Refs, Url));
        Assert.Equal(new GitTarget(GitSourceKind.Tag, "v1.2.0-rc.1"), GitScripts.Resolve(GitSource.LatestRelease("v*", true), Refs, Url));
    }

    [Fact]
    public void No_matching_release_explains_that_pre_releases_were_left_out()
    {
        var refs = Refs with { Tags = [new GitRef("v2.0.0-beta", new string('6', 40))] };

        var ex = Assert.Throws<OperationException>(() => GitScripts.Resolve(GitSource.LatestRelease(), refs, Url));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Contains("pre-releases", ex.Message);
        Assert.Contains("v2.0.0-beta", ex.Message);
    }

    [Fact]
    public void A_commit_is_taken_as_given()
    {
        Assert.Equal(new GitTarget(GitSourceKind.Commit, "3f2a9c1"), GitScripts.Resolve(GitSource.Commit("3f2a9c1"), Refs, Url));
    }

    [Fact]
    public void Long_ref_lists_are_cut_to_ten_names()
    {
        var refs = Refs with { Branches = Enumerable.Range(1, 15).Select(i => new GitRef($"b{i}", new string('1', 40))).ToList() };

        var ex = Assert.Throws<OperationException>(() => GitScripts.Resolve(GitSource.Branch("main"), refs, Url));

        Assert.Contains("b10, … (15 in total)", ex.Message);
        Assert.DoesNotContain("b11", ex.Message);
    }

    // ── Scripts ────────────────────────────────────────────────────────────

    [Fact]
    public void Cloning_a_branch_or_tag_uses_clone_branch_and_records_the_source()
    {
        var script = GitScripts.Clone(Url, Dir, new GitTarget(GitSourceKind.Tag, "v1.1.0"), GitSource.LatestRelease("v*"));

        Assert.Contains($"clone --branch 'v1.1.0' -- '{Url}' '{Dir}'", script);
        Assert.Contains($"git -C '{Dir}' config lrw.track 'latest-release:v*'", script);
        Assert.Contains(DeployPaths.GitSshEnv, script);
    }

    [Fact]
    public void Cloning_a_commit_checks_it_out_detached_and_cleans_up_when_it_is_missing()
    {
        var script = GitScripts.Clone(Url, Dir, new GitTarget(GitSourceKind.Commit, "3f2a9c1"), GitSource.Commit("3f2a9c1"));

        Assert.Contains("clone --no-checkout --", script);
        Assert.Contains("cat-file -e '3f2a9c1^{commit}'", script);
        Assert.Contains($"rm -rf -- '{Dir}'; exit {GitScripts.ExitMissingRef}", script);
        Assert.Contains("checkout -q --detach '3f2a9c1' --", script);
    }

    [Fact]
    public void Values_are_shell_quoted()
    {
        var script = GitScripts.Clone("https://example.com/a'b.git", Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main"));

        Assert.Contains("'https://example.com/a'\\''b.git'", script);
    }

    [Fact]
    public void Fetching_refuses_changed_files_unless_forced()
    {
        var careful = GitScripts.FetchRefs(Dir, force: false);
        var forced = GitScripts.FetchRefs(Dir, force: true);

        Assert.Contains($"exit {GitScripts.ExitLocalChanges}", careful);
        Assert.DoesNotContain("status --porcelain", forced);
        foreach (var script in new[] { careful, forced })
        {
            Assert.StartsWith($"cd '{Dir}' || exit {GitScripts.ExitNotFound}", script);
            Assert.Contains("fetch --prune --tags --force origin", script);
            Assert.Contains($"echo '{GitScripts.RefsMarker}'", script);
            Assert.Contains("git show-ref -d", script);
        }
    }

    [Fact]
    public void A_branch_moves_only_by_fast_forward()
    {
        var script = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main"), force: false);

        Assert.Contains("rev-parse --verify -q 'refs/remotes/origin/main'", script);
        Assert.Contains("merge-base --is-ancestor HEAD 'refs/remotes/origin/main'", script);
        Assert.Contains($"exit {GitScripts.ExitDiverged}", script);
        Assert.Contains("merge -q --ff-only 'refs/remotes/origin/main'", script);
        Assert.Contains("checkout -q --track -b 'main' 'refs/remotes/origin/main'", script);
        Assert.DoesNotContain("reset", script);
        Assert.EndsWith("git config lrw.track 'branch:main'", script);
    }

    [Fact]
    public void A_forced_branch_checkout_resets_to_origin()
    {
        var script = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main"), force: true);

        Assert.Contains("checkout -q -f 'main' --", script);
        Assert.Contains("reset -q --hard 'refs/remotes/origin/main'", script);
        Assert.DoesNotContain("merge-base", script);
    }

    [Fact]
    public void A_tag_is_checked_out_detached_by_its_full_ref()
    {
        var script = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Tag, "v1.0.0"), GitSource.Tag("v1.0.0"), force: false);

        Assert.Contains("rev-parse --verify -q 'refs/tags/v1.0.0^{commit}'", script);
        Assert.Contains($"exit {GitScripts.ExitMissingRef}", script);
        Assert.Contains("checkout -q --detach 'refs/tags/v1.0.0' --", script);
        Assert.EndsWith("git config lrw.track 'tag:v1.0.0'", script);
    }

    [Fact]
    public void A_commit_is_checked_out_detached()
    {
        var script = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Commit, "3f2a9c1"), GitSource.Commit("3f2a9c1"), force: true);

        Assert.Contains("cat-file -e '3f2a9c1^{commit}'", script);
        Assert.Contains("checkout -q -f --detach '3f2a9c1' --", script);
    }

    [Fact]
    public void Listing_a_remote_asks_for_the_default_branch_branches_and_tags()
    {
        Assert.EndsWith($"git ls-remote --symref '{Url}' HEAD 'refs/heads/*' 'refs/tags/*' 2>&1", GitScripts.LsRemote(Url));
        Assert.Contains($"git -C '{Dir}' ls-remote --symref origin HEAD", GitScripts.LsRemoteOfClone(Dir));
    }
}
