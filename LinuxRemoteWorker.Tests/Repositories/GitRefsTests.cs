using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Tests.Repositories;

public class GitRefsTests
{
    private static readonly string C1 = new('1', 40);
    private static readonly string C2 = new('2', 40);
    private static readonly string C3 = new('3', 40);
    private static readonly string C4 = new('4', 40);
    private static readonly string C5 = new('5', 40);

    [Fact]
    public void Reads_ls_remote_with_symref()
    {
        var output = string.Join("\n",
            "ref: refs/heads/main\tHEAD",
            $"{C1}\tHEAD",
            $"{C1}\trefs/heads/main",
            $"{C2}\trefs/heads/feature/login",
            $"{C3}\trefs/tags/v1.0.0",
            $"{C4}\trefs/tags/v1.1.0",
            $"{C5}\trefs/tags/v1.1.0^{{}}");

        var refs = GitRefs.Parse(output);

        Assert.Equal("main", refs.DefaultBranch);
        Assert.Equal([new GitRef("main", C1), new GitRef("feature/login", C2)], refs.Branches);
        Assert.Equal([new GitRef("v1.0.0", C3), new GitRef("v1.1.0", C5)], refs.Tags);
    }

    [Fact]
    public void An_annotated_tag_resolves_to_the_commit_it_tags()
    {
        var refs = GitRefs.Parse($"{C4}\trefs/tags/v1.1.0\n{C5}\trefs/tags/v1.1.0^{{}}");

        Assert.Equal(C5, refs.Tag("v1.1.0")?.Commit);
    }

    [Fact]
    public void Reads_show_ref_of_a_clone_where_origin_wins_over_local_branches()
    {
        var output = string.Join("\r\n",
            $"{C1} refs/heads/main",
            $"{C2} refs/remotes/origin/HEAD",
            $"{C3} refs/remotes/origin/main",
            $"{C4} refs/remotes/origin/develop",
            $"{C5} refs/tags/v2.0.0",
            "");

        var refs = GitRefs.Parse(output);

        Assert.Null(refs.DefaultBranch);
        Assert.Equal([new GitRef("main", C3), new GitRef("develop", C4)], refs.Branches);
        Assert.Null(refs.Branch("HEAD"));
        Assert.Equal([new GitRef("v2.0.0", C5)], refs.Tags);
    }

    [Fact]
    public void Ignores_lines_that_are_not_refs()
    {
        var output = string.Join("\n",
            "Warning: Permanently added 'github.com' (ED25519) to the list of known hosts.",
            "",
            "fatal: not a commit",
            $"{C1}\trefs/pull/12/head",
            $"{C1}\trefs/stash",
            "zzzzzzzzzz\trefs/heads/bogus",
            $"{C2}\trefs/heads/main");

        var refs = GitRefs.Parse(output);

        Assert.Equal([new GitRef("main", C2)], refs.Branches);
        Assert.Empty(refs.Tags);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData("abc", "abc")]
    [InlineData("3f2a9c1", "3f2a9c1")]
    [InlineData("3f2a9c1d4e5f60718293a4b5c6d7e8f901234567", "3f2a9c1")]
    public void Short_shows_seven_characters(string? commit, string expected)
    {
        Assert.Equal(expected, GitRefs.Short(commit));
    }
}
