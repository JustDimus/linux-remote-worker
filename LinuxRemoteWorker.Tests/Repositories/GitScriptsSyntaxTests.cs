using LinuxRemoteWorker.Modules.Repositories;
using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Tests.Repositories;

/// <summary>
/// `bash -n` parses the generated scripts without running anything (see <see cref="TestBash"/> for which bash).
/// </summary>
public class GitScriptsSyntaxTests
{
    private const string Url = "git@github.com:acme/shop.git";
    private const string Dir = "/srv/lrw/repos/shop";

    private static readonly Dictionary<string, string> Scripts = new()
    {
        ["ls-remote"] = GitScripts.LsRemote(Url),
        ["ls-remote-clone"] = GitScripts.LsRemoteOfClone(Dir),
        ["clone-branch"] = GitScripts.Clone(Url, Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main")),
        ["clone-commit"] = GitScripts.Clone(Url, Dir, new GitTarget(GitSourceKind.Commit, "3f2a9c1"), GitSource.Commit("3f2a9c1")),
        ["fetch"] = GitScripts.FetchRefs(Dir, force: false),
        ["fetch-force"] = GitScripts.FetchRefs(Dir, force: true),
        ["checkout-branch"] = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main"), force: false),
        ["checkout-branch-force"] = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Branch, "main"), GitSource.Branch("main"), force: true),
        ["checkout-tag"] = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Tag, "v1.0.0"), GitSource.LatestRelease("v*", true), force: false),
        ["checkout-commit"] = GitScripts.Checkout(Dir, new GitTarget(GitSourceKind.Commit, "3f2a9c1"), GitSource.Commit("3f2a9c1"), force: true),
        ["list"] = RepoListing.Script("/srv/lrw/repos/*/"),
        ["list-one"] = RepoListing.Script("'/srv/lrw/repos/it'\\''s'"),
        ["security-status"] = SshdConfig.StatusScript("o'neil"),
        ["security-effective"] = SshdConfig.EffectiveSettingsCommand("root"),
        ["security-list-keys"] = SshdConfig.ListKeysScript(["/root/.ssh/authorized_keys", "/home/o'neil/.ssh/authorized_keys"]),
        ["security-create-key"] = SshdConfig.CreateKeyScript("lrw-me@PC-20261027"),
        ["security-remove-key-dir"] = SshdConfig.RemoveKeyDirScript("/tmp/lrw-key.Ab12Cd34"),
        ["security-authorize"] = SshdConfig.AuthorizeScript("/home/o'neil/.ssh/authorized_keys", "o'neil", "ssh-ed25519 AAAA c"),
        ["security-unauthorize"] = SshdConfig.UnauthorizeScript("/root/.ssh/authorized_keys", "ssh-ed25519 AAAA c"),
        ["security-test-config"] = SshdConfig.TestConfigCommand,
        ["security-reload"] = SshdConfig.ReloadCommand,
        ["security-write-policy"] = "mkdir -p /etc/ssh/sshd_config.d && " +
                                    LinuxRemoteWorker.Core.Shell.WriteFile(SshdConfig.PolicyFile, SshdConfig.PolicyContent),
        ["security-write-root-policy"] = LinuxRemoteWorker.Core.Shell.WriteFile(SshdConfig.RootPolicyFile, SshdConfig.RootPolicyContent),
        ["security-create-admin"] = SshdConfig.CreateAdminScript("admin"),
        ["security-remove-admin"] = SshdConfig.RemoveAdminScript("admin"),
        ["security-copy-keys"] = SshdConfig.CopyKeysScript(["/root/.ssh/authorized_keys", "/root/.ssh/authorized_keys2"], "/home/admin/.ssh/authorized_keys", "admin"),
        ["sudo-wrapped-heredoc"] = LinuxRemoteWorker.Core.SshService.SudoCommand(
            LinuxRemoteWorker.Core.Shell.WriteFile("/etc/nginx/sites-available/it's", "server_name $host; # 'quoted'\n")),
        ["sudo-upload"] = LinuxRemoteWorker.Core.SshService.SudoCommand(LinuxRemoteWorker.Core.SshService.UploadScript("/etc/o'neil.conf", overwrite: false)),
        ["sudo-listing"] = LinuxRemoteWorker.Core.SshService.SudoCommand(LinuxRemoteWorker.Core.RemoteListing.Command("/var/log/o'neil"))
    };

    public static TheoryData<string> Names() => new(Scripts.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Generated_scripts_are_valid_bash(string name)
    {
        if (TestBash.Path is not { } bash) return;

        var (exitCode, _, errors) = TestBash.Run(bash, Scripts[name], "-n");

        Assert.True(exitCode == 0, $"{name}: {errors}");
    }
}
