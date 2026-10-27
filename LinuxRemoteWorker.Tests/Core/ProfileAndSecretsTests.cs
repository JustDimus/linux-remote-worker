using System.Text.Json;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Core;

public class ProfileAndSecretsTests
{
    [Fact]
    public void Profiles_saved_before_passwords_existed_log_in_with_their_key()
    {
        const string old = """[{ "Id": "6f1c2a3b-0000-4000-8000-000000000001", "Name": "prod", "Host": "203.0.113.10", "Username": "root", "PrivateKeyPath": "C:\\keys\\id" }]""";

        var profile = Assert.Single(JsonSerializer.Deserialize<List<ConnectionProfile>>(old)!);

        Assert.Equal(SshAuthMethod.Key, profile.AuthMethod);
        Assert.False(profile.HasSavedPassword);
        Assert.Equal(@"C:\keys\id", profile.PrivateKeyPath);
    }

    [Fact]
    public void The_login_method_is_stored_by_name_and_an_absent_password_not_at_all()
    {
        var json = JsonSerializer.Serialize(new ConnectionProfile { Name = "p", AuthMethod = SshAuthMethod.Password });

        Assert.Contains("\"AuthMethod\":\"Password\"", json);
        Assert.DoesNotContain("ProtectedPassword", json);
        Assert.DoesNotContain("HasSavedPassword", json);
    }

    [Fact]
    public void Switching_a_profile_to_a_key_drops_its_saved_password()
    {
        var profile = new ConnectionProfile { AuthMethod = SshAuthMethod.Password, ProtectedPassword = "blob" };

        profile.UseKey(@"C:\keys\new");

        Assert.Equal(SshAuthMethod.Key, profile.AuthMethod);
        Assert.Equal(@"C:\keys\new", profile.PrivateKeyPath);
        Assert.Null(profile.ProtectedPassword);
    }

    [Fact]
    public void A_saved_password_is_encrypted_and_comes_back()
    {
        if (!PasswordProtector.IsSupported) return;

        var profile = new ConnectionProfile();
        profile.UsePassword("s3cret-pässword");

        Assert.Equal(SshAuthMethod.Password, profile.AuthMethod);
        Assert.DoesNotContain("s3cret", profile.ProtectedPassword);
        Assert.Equal("s3cret-pässword", PasswordProtector.Unprotect(profile.ProtectedPassword));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    public void A_damaged_saved_password_reads_as_none(string? blob)
    {
        Assert.Null(PasswordProtector.Unprotect(blob));
    }

    [Fact]
    public void Credentials_never_print_their_secrets()
    {
        var withPassword = SshCredentials.WithPassword("203.0.113.10", "deploy", "hunter2");
        var withKey = SshCredentials.WithKey("203.0.113.10", "root", @"C:\keys\id", "phrase");
        var target = new ConnectionTarget("203.0.113.10", "deploy", "", null, "prod", SshAuthMethod.Password, "hunter2");

        Assert.Equal("deploy@203.0.113.10 (password)", withPassword.ToString());
        Assert.DoesNotContain("phrase", withKey.ToString());
        Assert.DoesNotContain("hunter2", target.ToString());
        Assert.Equal("hunter2", target.Credentials.Password);
    }

    [Fact]
    public void An_empty_passphrase_means_none()
    {
        Assert.Null(SshCredentials.WithKey("h", "u", "k", "").Passphrase);
    }
}
