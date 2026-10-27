using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Tests.Security;

public class SshdConfigTests
{
    [Fact]
    public void Reads_the_login_settings_from_sshd_T()
    {
        var settings = SshdSettings.Parse([
            "port 22",
            "passwordauthentication no",
            "kbdinteractiveauthentication no",
            "pubkeyauthentication yes",
            "permitrootlogin without-password",
            "authorizedkeysfile .ssh/authorized_keys .ssh/authorized_keys2"
        ]);

        Assert.False(settings.PasswordAuthentication);
        Assert.False(settings.KbdInteractiveAuthentication);
        Assert.True(settings.PubkeyAuthentication);
        Assert.Equal("without-password", settings.PermitRootLogin);
        Assert.Equal([".ssh/authorized_keys", ".ssh/authorized_keys2"], settings.AuthorizedKeysFiles);
        Assert.False(settings.PasswordLoginAllowed);
    }

    [Fact]
    public void Older_OpenSSH_reports_challenge_response_instead_of_keyboard_interactive()
    {
        var settings = SshdSettings.Parse(["passwordauthentication no", "challengeresponseauthentication yes"]);

        Assert.True(settings.KbdInteractiveAuthentication);
        Assert.True(settings.PasswordLoginAllowed);
    }

    [Theory]
    [InlineData("yes", "no", true)]
    [InlineData("no", "yes", true)]
    [InlineData("no", "no", false)]
    public void Passwords_get_in_while_either_method_is_on(string password, string interactive, bool allowed)
    {
        var settings = SshdSettings.Parse([$"passwordauthentication {password}", $"kbdinteractiveauthentication {interactive}"]);

        Assert.Equal(allowed, settings.PasswordLoginAllowed);
    }

    [Fact]
    public void Unreported_settings_count_as_password_login_allowed()
    {
        Assert.True(SshdSettings.Parse(["garbage", ""]).PasswordLoginAllowed);
    }

    [Fact]
    public void Authorized_keys_files_are_expanded_like_sshd_does()
    {
        var files = SshdConfig.ExpandAuthorizedKeysFiles(
            [".ssh/authorized_keys", "/etc/ssh/keys/%u", "%h/.ssh/extra_%U", "none", "/x/100%%"], "deploy", "1000", "/home/deploy");

        Assert.Equal(
            ["/home/deploy/.ssh/authorized_keys", "/etc/ssh/keys/deploy", "/home/deploy/.ssh/extra_1000", "/x/100%"],
            files);
    }

    [Fact]
    public void Without_a_reported_file_sshd_uses_the_default()
    {
        Assert.Equal(["/root/.ssh/authorized_keys"], SshdConfig.ExpandAuthorizedKeysFiles([], "root", "0", "/root/"));
        Assert.Empty(SshdConfig.ExpandAuthorizedKeysFiles(["none"], "root", "0", "/root"));
    }

    [Theory]
    [InlineData("256 SHA256:abc123 lrw-me@PC-20261027 (ED25519)", 256, "SHA256:abc123", "lrw-me@PC-20261027", "ED25519")]
    [InlineData("3072 SHA256:xyz deploy key for ci (RSA)", 3072, "SHA256:xyz", "deploy key for ci", "RSA")]
    [InlineData("256 SHA256:q1 no comment (ECDSA)", 256, "SHA256:q1", "", "ECDSA")]
    public void Fingerprint_lines_are_read(string line, int bits, string fingerprint, string comment, string type)
    {
        Assert.Equal(new AuthorizedKey("", bits, fingerprint, comment, type), SshdConfig.ParseFingerprint(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/root/.ssh/authorized_keys is not a public key file.")]
    [InlineData("256 abc comment (ED25519)")]
    public void Other_lines_are_not_fingerprints(string line)
    {
        Assert.Null(SshdConfig.ParseFingerprint(line));
    }

    [Fact]
    public void Keys_are_attributed_to_their_file()
    {
        var keys = SshdConfig.ParseKeys(
            "@@file /root/.ssh/authorized_keys\n256 SHA256:a one (ED25519)\n3072 SHA256:b two (RSA)\n@@file /etc/ssh/keys/root\n256 SHA256:c three (ED25519)\n");

        Assert.Equal(["/root/.ssh/authorized_keys", "/root/.ssh/authorized_keys", "/etc/ssh/keys/root"], keys.Select(k => k.File));
        Assert.Equal(["one", "two", "three"], keys.Select(k => k.Comment));
    }

    [Fact]
    public void The_status_script_output_is_read()
    {
        var status = SshdConfig.ParseStatus(string.Join("\r\n",
            "@@user deploy", "@@uid 1000", "@@home /home/deploy",
            "@@sshd passwordauthentication yes", "@@sshd pubkeyauthentication yes",
            "@@dropins", "@@keygen", ""));

        Assert.Equal(("deploy", "1000", "/home/deploy"), (status.User, status.Uid, status.Home));
        Assert.True(status.Settings.PasswordAuthentication);
        Assert.True(status.DropInsSupported);
        Assert.True(status.HasKeygen);
        Assert.False(status.PolicyInstalled);
    }

    [Theory]
    [InlineData("/tmp/lrw-key.Ab12Cd34", true)]
    [InlineData("/tmp/lrw-key.", false)]
    [InlineData("/tmp/lrw-key.x/../../etc", false)]
    [InlineData("/", false)]
    [InlineData("/tmp/other.Ab12", false)]
    public void Only_the_apps_own_temporary_key_directories_are_ever_removed(string dir, bool ok)
    {
        Assert.Equal(ok, SshdConfig.IsKeyDir(dir));
    }

    [Fact]
    public void The_policy_turns_off_both_password_methods_and_sorts_first()
    {
        Assert.Contains("PasswordAuthentication no", SshdConfig.PolicyContent);
        Assert.Contains("ChallengeResponseAuthentication no", SshdConfig.PolicyContent);
        Assert.StartsWith("/etc/ssh/sshd_config.d/00-", SshdConfig.PolicyFile);
    }

    [Fact]
    public void Scripts_quote_what_comes_from_outside()
    {
        var authorize = SshdConfig.AuthorizeScript("/home/o'neil/.ssh/authorized_keys", "o'neil", "ssh-ed25519 AAAA lrw-x");

        Assert.Contains("f='/home/o'\\''neil/.ssh/authorized_keys'", authorize);
        Assert.Contains("chown 'o'\\''neil':", authorize);
        Assert.Contains("printf '%s\\n' 'ssh-ed25519 AAAA lrw-x' >> \"$f\"", authorize);
        Assert.Contains("-C 'lrw-me@PC-20261027'", SshdConfig.CreateKeyScript("lrw-me@PC-20261027"));
        Assert.Contains("grep -vxF -- 'ssh-ed25519 AAAA lrw-x'", SshdConfig.UnauthorizeScript("/root/.ssh/authorized_keys", "ssh-ed25519 AAAA lrw-x"));
    }

    private static SecurityStatus Status(SshAuthMethod auth, bool passwordAllowed, bool dropIns = true) => new(
        "root", auth, auth == SshAuthMethod.Key ? @"C:\keys\id" : null, null,
        SshdSettings.Parse([$"passwordauthentication {(passwordAllowed ? "yes" : "no")}", "kbdinteractiveauthentication no"]),
        PolicyInstalled: false, DropInsSupported: dropIns, [], []);

    [Fact]
    public void Password_login_can_only_be_turned_off_from_a_key_login()
    {
        Assert.Null(Status(SshAuthMethod.Key, passwordAllowed: true).CannotDisablePasswordLogin);
        Assert.Contains("logged in with a password", Status(SshAuthMethod.Password, passwordAllowed: true).CannotDisablePasswordLogin);
    }

    [Fact]
    public void Nothing_to_turn_off_when_it_is_already_off_or_the_server_is_too_old()
    {
        Assert.Contains("already off", Status(SshAuthMethod.Key, passwordAllowed: false).CannotDisablePasswordLogin);
        Assert.Contains("sshd_config.d", Status(SshAuthMethod.Key, passwordAllowed: true, dropIns: false).CannotDisablePasswordLogin);
    }

    [Fact]
    public void The_default_key_path_is_in_the_users_ssh_folder_and_safe_as_a_file_name()
    {
        var path = SecurityService.DefaultKeyPath("2001:db8::1");

        Assert.Equal(".ssh", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith("lrw_2001_db8__1_ed25519", Path.GetFileName(path));
    }
}
