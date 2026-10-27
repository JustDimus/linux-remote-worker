using System.Net;
using System.Security.Cryptography;
using System.Text;
using LinuxRemoteWorker.Cli.Commands;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Update;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Cli;

public class ReleaseChannelTests
{
    [Theory]
    [InlineData("0.2.1\n", "0.2.1")]
    [InlineData("v0.2.1", "0.2.1")]
    [InlineData(" 12.0.7\r\n", "12.0.7")]
    public void A_version_is_read_from_its_text(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), ReleaseChannel.ParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.2")]
    [InlineData("0.2.1.4")]
    [InlineData("<!DOCTYPE html>")]
    public void Anything_else_is_not_a_version(string? text)
    {
        Assert.Null(ReleaseChannel.ParseVersion(text));
    }

    [Fact]
    public void The_hash_of_a_file_comes_from_sha256sum_output()
    {
        var sums = $"{new string('a', 64)}  lrw-linux-x64.tar.gz\n{new string('B', 64)} *install-cli.sh\r\n";

        Assert.Equal(new string('a', 64), ReleaseChannel.ExpectedHash(sums, "lrw-linux-x64.tar.gz"));
        Assert.Equal(new string('B', 64), ReleaseChannel.ExpectedHash(sums, "install-cli.sh"));
        Assert.Null(ReleaseChannel.ExpectedHash(sums, "install-cli.ps1"));
    }

    [Fact]
    public async Task The_newest_version_is_the_version_txt_of_the_latest_release()
    {
        var channel = Channel(new() { ["releases/latest/download/version.txt"] = Bytes("0.3.0\n") });

        Assert.Equal(new Version(0, 3, 0), await channel.LatestVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_matches_its_checksum_is_saved()
    {
        var script = Bytes("echo installed\n");
        var channel = Channel(new()
        {
            ["releases/download/v0.3.0/SHA256SUMS.txt"] = Bytes($"{Hash(script)}  install-cli.sh\n"),
            ["releases/download/v0.3.0/install-cli.sh"] = script
        });
        var folder = Directory.CreateTempSubdirectory("lrw-test-").FullName;
        try
        {
            var path = await channel.DownloadAsync(new Version(0, 3, 0), "install-cli.sh", folder, CancellationToken.None);

            Assert.Equal(script, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_that_does_not_match_or_is_not_listed_is_not_saved(bool listed)
    {
        var channel = Channel(new()
        {
            ["releases/download/v0.3.0/SHA256SUMS.txt"] = Bytes(listed ? $"{new string('0', 64)}  install-cli.sh\n" : ""),
            ["releases/download/v0.3.0/install-cli.sh"] = Bytes("tampered\n")
        });
        var folder = Directory.CreateTempSubdirectory("lrw-test-").FullName;
        try
        {
            var ex = await Assert.ThrowsAsync<OperationException>(() =>
                channel.DownloadAsync(new Version(0, 3, 0), "install-cli.sh", folder, CancellationToken.None));

            Assert.Equal(FailureKind.Failed, ex.Kind);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ReleaseChannel Channel(Dictionary<string, byte[]> files) => new(new HttpClient(new FakeReleases(files)));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    // Answers the release URLs whose end matches a key; anything else is 404
    private sealed class FakeReleases(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var file = files.FirstOrDefault(f => url.EndsWith("/" + f.Key, StringComparison.Ordinal));
            return Task.FromResult(file.Value is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

public class UpdateNotifierTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_check_is_due_once_a_day()
    {
        Assert.True(UpdateNotifier.IsDue(null, Now));
        Assert.False(UpdateNotifier.IsDue(Now.AddHours(-23), Now));
        Assert.True(UpdateNotifier.IsDue(Now.AddHours(-25), Now));
    }

    [Fact]
    public void A_check_recorded_in_the_future_is_due()
    {
        Assert.True(UpdateNotifier.IsDue(Now.AddDays(2), Now));
    }

    [Fact]
    public void Only_a_newer_release_is_mentioned()
    {
        var notice = UpdateNotifier.Notice(new Version(0, 2, 0), new Version(0, 2, 1));

        Assert.Equal("A new version of lrw is available: v0.2.1 (you have 0.2.0). Run: lrw update", notice);
        Assert.Null(UpdateNotifier.Notice(new Version(0, 2, 1), new Version(0, 2, 1)));
        Assert.Null(UpdateNotifier.Notice(new Version(0, 3, 0), new Version(0, 2, 1)));
    }
}

public class InstallLocationTests
{
    [Fact]
    public void Only_the_place_of_the_install_scripts_is_the_installed_copy()
    {
        var standard = InstallLocation.StandardPath;

        Assert.True(InstallLocation.IsStandard(standard, standard));
        Assert.False(InstallLocation.IsStandard(Path.Combine(Path.GetTempPath(), Path.GetFileName(standard)), standard));
        Assert.False(InstallLocation.IsStandard(null, standard));
    }

    [Fact]
    public void Replaced_copies_next_to_the_program_are_deleted_unless_still_in_use()
    {
        var folder = Directory.CreateTempSubdirectory("lrw-test-").FullName;
        string File_(string name) => Path.Combine(folder, name);
        try
        {
            foreach (var name in new[] { "lrw.exe", "lrw.exe.20261027120000.old", "lrw.exe.20261027130000.old", "notes.old" })
                File.WriteAllText(File_(name), name);

            using (new FileStream(File_("lrw.exe.20261027130000.old"), FileMode.Open, FileAccess.Read, FileShare.Read))
                InstallLocation.RemoveReplacedCopies(File_("lrw.exe"));

            Assert.True(File.Exists(File_("lrw.exe")));
            Assert.True(File.Exists(File_("notes.old")));
            Assert.False(File.Exists(File_("lrw.exe.20261027120000.old")));
            // Windows refuses to delete a file that is open, as it refuses the lrw.exe of a running MCP server
            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(File_("lrw.exe.20261027130000.old")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Windows_paths_compare_without_case()
    {
        if (!OperatingSystem.IsWindows()) return;
        var standard = InstallLocation.StandardPath;

        Assert.True(InstallLocation.IsStandard(standard.ToUpperInvariant(), standard));
    }
}

public class UpdateCommandLineTests
{
    private static readonly CommandCatalog Catalog = CommandCatalog.Build(typeof(UpdateCommands).Assembly);

    private static ParsedCommandLine Parse(params string[] args) => CommandLineParser.Parse(Catalog, args);

    [Fact]
    public void Update_is_a_one_word_command_of_the_command_line_only()
    {
        var parsed = Parse("update", "--check");

        Assert.Equal("update", parsed.Command?.FullPath);
        Assert.Equal(true, parsed.Values["check"]);
        Assert.DoesNotContain(Catalog.For(Surface.Mcp), c => c.Group == "update");
    }

    [Fact]
    public void Its_options_reach_the_command()
    {
        var parsed = Parse("update", "--version", "v0.3.0", "--force", "--mcp-profile", "prod", "--no-mcp", "--no-skill");

        Assert.Equal("v0.3.0", parsed.Values["version"]);
        Assert.Equal(true, parsed.Values["force"]);
        Assert.Equal("prod", parsed.Values["mcpProfile"]);
        Assert.Equal(false, parsed.Values["mcp"]);
        Assert.Equal(false, parsed.Values["skill"]);
    }

    [Fact]
    public void A_group_name_alone_is_still_no_command()
    {
        Assert.Null(Parse("nginx").Command);
    }

    [Fact]
    public void The_install_script_gets_its_own_option_names()
    {
        var target = new Version(0, 3, 0);

        Assert.Equal(["-Version", "v0.3.0", "-Force", "-McpProfile", "prod", "-NoMcp", "-NoSkill", "-NoPath"],
            UpdateCommands.ScriptArguments(windows: true, target, force: true, mcpProfile: "prod", mcp: false, skill: false, path: false));
        Assert.Equal(["--version", "v0.3.0"],
            UpdateCommands.ScriptArguments(windows: false, target, force: false, mcpProfile: null, mcp: true, skill: true, path: true));
        Assert.Equal(["--version", "v0.3.0", "--force", "--mcp-profile", "prod", "--no-mcp", "--no-skill", "--no-path"],
            UpdateCommands.ScriptArguments(windows: false, target, force: true, mcpProfile: "prod", mcp: false, skill: false, path: false));
    }
}
