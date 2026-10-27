using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Core;

public sealed class SudoLayerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lrw-sudo-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Commands_go_through_non_interactive_sudo_with_roots_home()
    {
        Assert.Equal("sudo -n -H -- bash -c 'systemctl reload ssh'", SshService.SudoCommand("systemctl reload ssh"));
    }

    [Fact]
    public void A_fresh_service_runs_commands_unchanged_until_it_knows_the_login_is_not_root()
    {
        var ssh = new SshService();

        Assert.False(ssh.UsesSudo);
        Assert.Equal("root", ssh.Privileges);
        Assert.Equal("id -u", ssh.Elevate("id -u"));
    }

    // What sudo hands to bash -c must be exactly the original script, whatever it contains
    [Fact]
    public void Wrapped_scripts_run_exactly_as_written()
    {
        if (TestBash.Path is not { } bash) return;
        var target = Path.Combine(_dir, "out.txt").Replace('\\', '/');
        var script = string.Join("\n",
            "x='it'\\''s $HOME and `date`'",
            "printf '%s\\n' \"$x\"",
            Shell.WriteFile(target, "server_name $host;\nline 'two' \"three\" \\back\n"),
            $"cat '{target}'");

        var direct = TestBash.Run(bash, script);
        var wrapped = TestBash.Run(bash, SshService.SudoCommand(script)["sudo -n -H -- ".Length..]);

        Assert.Equal(0, wrapped.ExitCode);
        Assert.Equal(direct.Output, wrapped.Output);
        Assert.Contains("it's $HOME and `date`", wrapped.Output);
        Assert.Contains("server_name $host;", wrapped.Output);
    }

    [Fact]
    public void Uploads_overwrite_in_place_or_create_and_can_refuse_existing_files()
    {
        if (TestBash.Path is not { } bash) return;
        var file = Path.Combine(_dir, "upload.conf");
        var path = file.Replace('\\', '/');

        var created = Run(bash, SshService.UploadScript(path, overwrite: false), "first\n");
        var refused = Run(bash, SshService.UploadScript(path, overwrite: false), "second\n");
        var replaced = Run(bash, SshService.UploadScript(path, overwrite: true), "third\n");

        Assert.Equal(0, created);
        Assert.Equal(SshService.ExitFileExists, refused);
        Assert.Equal(0, replaced);
        Assert.Equal("third\n", File.ReadAllText(file));
    }

    // The script reads the file content from stdin, as the upload does through the command's input stream
    private static int Run(string bash, string script, string stdin) =>
        TestBash.Run(bash, $"{script} <<'LRW_STDIN'\n{stdin}LRW_STDIN\n").ExitCode;

    [Fact]
    public void Directory_listings_from_find_are_read()
    {
        var entries = RemoteListing.Parse("/var/log/", "d\t4096\t1729955100.5\tnginx\nf\t1234\t1729955200.0000000000\tsyslog\nbad line\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal(new RemoteFileInfo("nginx", "/var/log/nginx", true, 4096, DateTime.UnixEpoch.AddSeconds(1729955100.5)), entries[0]);
        Assert.Equal("/var/log/syslog", entries[1].FullName);
        Assert.False(entries[1].IsDirectory);
    }

    [Fact]
    public void The_listing_command_quotes_the_directory()
    {
        Assert.StartsWith("find '/home/o'\\''neil' -mindepth 1 -maxdepth 1 -printf", RemoteListing.Command("/home/o'neil"));
    }
}
