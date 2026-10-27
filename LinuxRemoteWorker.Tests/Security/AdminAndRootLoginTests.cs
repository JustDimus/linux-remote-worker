using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Tests.Security;

public class AdminAndRootLoginTests
{
    private static SecurityStatus Status(string user, SshAuthMethod auth = SshAuthMethod.Key, string privileges = "sudo",
        string? rootLogin = "prohibit-password", bool dropIns = true) => new(
        user, auth, auth == SshAuthMethod.Key ? @"C:\keys\id" : null, null,
        SshdSettings.Parse(["passwordauthentication no", "kbdinteractiveauthentication no"]),
        PolicyInstalled: true, DropInsSupported: dropIns, [], [], privileges, rootLogin);

    [Fact]
    public void Root_login_can_be_turned_off_from_an_admin_users_key_login_with_sudo()
    {
        Assert.Null(Status("admin").CannotDisableRootLogin);
    }

    [Fact]
    public void Root_login_cannot_be_turned_off_while_logged_in_as_root()
    {
        Assert.Contains("logged in as root", Status("root", privileges: "root").CannotDisableRootLogin);
    }

    [Fact]
    public void Root_login_needs_a_key_login_and_working_sudo()
    {
        Assert.Contains("SSH key", Status("admin", auth: SshAuthMethod.Password).CannotDisableRootLogin);
        Assert.Contains("no passwordless sudo", Status("admin", privileges: "none").CannotDisableRootLogin);
        Assert.Contains("sshd_config.d", Status("admin", dropIns: false).CannotDisableRootLogin);
    }

    [Theory]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData("yes", true)]
    [InlineData("prohibit-password", true)]
    [InlineData("without-password", true)]
    [InlineData(null, true)]
    public void Only_PermitRootLogin_no_keeps_root_out(string? value, bool allowed)
    {
        var status = Status("admin", rootLogin: value);

        Assert.Equal(allowed, status.RootLoginAllowed);
        if (!allowed) Assert.Contains("already off", status.CannotDisableRootLogin);
    }

    [Fact]
    public void The_status_reads_roots_own_setting_and_the_apps_root_policy()
    {
        var status = SshdConfig.ParseStatus("@@user admin\n@@sshd permitrootlogin yes\n@@root permitrootlogin no\n@@rootpolicy\n");

        Assert.Equal("no", status.RootLogin);
        Assert.Equal("yes", status.Settings.PermitRootLogin);
        Assert.True(status.RootPolicyInstalled);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("deploy_2", true)]
    [InlineData("_svc", true)]
    [InlineData("a-b", true)]
    [InlineData("root", false)]
    [InlineData("Admin", false)]
    [InlineData("1admin", false)]
    [InlineData("-admin", false)]
    [InlineData("ad min", false)]
    [InlineData("admin;reboot", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void Only_plain_lower_case_user_names_are_created(string name, bool valid)
    {
        Assert.Equal(valid, SecurityService.ValidateNewUserName(name) == null);
    }

    [Fact]
    public void User_names_longer_than_32_characters_are_refused()
    {
        Assert.Null(SecurityService.ValidateNewUserName(new string('a', 32)));
        Assert.NotNull(SecurityService.ValidateNewUserName(new string('a', 33)));
    }

    [Fact]
    public void The_admin_gets_passwordless_sudo_checked_by_visudo_and_no_usable_password()
    {
        var script = SshdConfig.CreateAdminScript("admin");

        Assert.Contains("useradd -m -s /bin/bash", script);
        Assert.Contains("usermod -p '*' 'admin'", script);
        Assert.Contains("'admin ALL=(ALL:ALL) NOPASSWD: ALL'", script);
        Assert.Contains("'Defaults:admin !requiretty'", script);
        Assert.Contains("visudo -cf \"$t\"", script);
        Assert.Contains("install -m 440 -o root -g root \"$t\" '/etc/sudoers.d/lrw-admin'", script);
        Assert.Contains($"exit {SshdConfig.ExitUserExists}", script);
    }

    [Fact]
    public void Removing_an_admin_takes_its_home_and_sudoers_file()
    {
        var script = SshdConfig.RemoveAdminScript("admin");

        Assert.Contains("userdel -r 'admin'", script);
        Assert.Contains("rm -f '/etc/sudoers.d/lrw-admin'", script);
    }

    [Fact]
    public void Root_login_is_turned_off_in_its_own_drop_in()
    {
        Assert.Equal("PermitRootLogin no", SshdConfig.RootPolicyContent.Split('\n').Single(l => l.StartsWith("Permit")));
        Assert.NotEqual(SshdConfig.PolicyFile, SshdConfig.RootPolicyFile);
        Assert.StartsWith("/etc/ssh/sshd_config.d/00-", SshdConfig.RootPolicyFile);
    }

    [Fact]
    public void Settings_are_read_for_the_named_user_not_whoever_runs_the_command()
    {
        Assert.Contains("'user=admin,host=localhost,addr=127.0.0.1'", SshdConfig.EffectiveSettingsCommand("admin"));
        Assert.Contains("u='admin'", SshdConfig.StatusScript("admin"));
        Assert.DoesNotContain("id -un", SshdConfig.StatusScript("admin"));
    }

    [Fact]
    public void The_default_admin_key_is_named_after_server_and_user()
    {
        Assert.StartsWith("lrw_203.0.113.10_admin_ed25519", Path.GetFileName(SecurityService.DefaultKeyPath("203.0.113.10", "admin")));
    }
}
