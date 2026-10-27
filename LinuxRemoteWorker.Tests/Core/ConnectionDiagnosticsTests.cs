using LinuxRemoteWorker.Core;
using Renci.SshNet.Common;

namespace LinuxRemoteWorker.Tests.Core;

public class ConnectionDiagnosticsTests
{
    private static readonly SshCredentials Password = SshCredentials.WithPassword("203.0.113.10", "deploy", "pw");
    private static readonly SshCredentials Key = SshCredentials.WithKey("203.0.113.10", "root", @"C:\keys\id");

    [Fact]
    public void A_password_login_needs_a_password_but_no_key()
    {
        Assert.Equal("Password is empty", ConnectionDiagnostics.PreflightCheck(Password with { Password = "" })?.Summary);
        Assert.Null(ConnectionDiagnostics.PreflightCheck(Password));
    }

    [Fact]
    public void A_key_login_without_a_key_suggests_password_login_too()
    {
        var problem = ConnectionDiagnostics.PreflightCheck(Key with { KeyPath = "" });

        Assert.Equal("Private key is not selected", problem?.Summary);
        Assert.Contains("password login", problem!.Hint);
    }

    [Fact]
    public void Host_and_user_are_checked_for_both_methods()
    {
        Assert.Equal("Host is empty", ConnectionDiagnostics.PreflightCheck(Password with { Host = " " })?.Summary);
        Assert.Equal("Username is empty", ConnectionDiagnostics.PreflightCheck(Key with { Username = "" })?.Summary);
    }

    [Fact]
    public void A_rejected_password_is_explained_as_such()
    {
        var problem = ConnectionDiagnostics.Explain(new SshAuthenticationException("Permission denied (password)."), Password);

        Assert.Equal("The server rejected the password", problem.Summary);
        Assert.Contains("fail2ban", problem.Hint);
    }

    [Fact]
    public void A_server_that_only_takes_keys_is_recognised()
    {
        var problem = ConnectionDiagnostics.Explain(
            new SshAuthenticationException("No suitable authentication method found to complete authentication (publickey)."), Password);

        Assert.Equal("This server does not accept passwords", problem.Summary);
    }

    [Fact]
    public void A_rejected_key_keeps_its_own_explanation()
    {
        var problem = ConnectionDiagnostics.Explain(new SshAuthenticationException("Permission denied (publickey)."), Key);

        Assert.Equal("The server rejected the key", problem.Summary);
    }

    [Fact]
    public void A_changed_server_key_warns_about_interception()
    {
        var known = new KnownHostKey("ssh-ed25519", "SHA256:old", new DateTime(2026, 10, 1));
        var changed = new HostKeyChangedException("203.0.113.10", known, new HostKey("ssh-ed25519", "SHA256:new"));

        var problem = ConnectionDiagnostics.Explain(changed, Password);

        Assert.Equal("The server's identity changed", problem.Summary);
        Assert.Contains("SHA256:new", problem.Detail);
        Assert.Contains("SHA256:old", problem.Detail);
        Assert.Contains("intercepting", problem.Hint);
    }

    [Theory]
    [InlineData("WARNING: Your password has expired.\nPassword change required but no TTY available.")]
    [InlineData("You are required to change your password immediately (administrator enforced)")]
    public void An_expired_password_is_recognised_from_what_the_server_writes(string serverText)
    {
        var refused = SessionRefusedException.From("ubuntu", "193.0.2.10", serverText, "exit 1", null);

        var problem = ConnectionDiagnostics.Explain(refused, Password with { Username = "ubuntu", Host = "193.0.2.10" });

        Assert.True(refused.PasswordExpired);
        Assert.Equal("The password has to be changed first", problem.Summary);
        Assert.Contains("ssh ubuntu@193.0.2.10", problem.Hint);
    }

    [Fact]
    public void A_session_closed_without_a_reason_suggests_both_causes()
    {
        var refused = SessionRefusedException.From("git", "193.0.2.10", "", "Channel was closed.", null);

        var problem = ConnectionDiagnostics.Explain(refused, Password);

        Assert.False(refused.PasswordExpired);
        Assert.Equal("The server accepted the login but closed the session", problem.Summary);
        Assert.Contains("expired", problem.Hint);
        Assert.Contains("nologin", problem.Hint);
    }

    [Fact]
    public void The_clipboard_report_names_the_login_method_not_the_password()
    {
        var text = new ConnectionProblem("s", "d", "h").ToClipboardText(Password);

        Assert.Contains("Login:   password", text);
        Assert.DoesNotContain("pw\n", text);
    }
}
