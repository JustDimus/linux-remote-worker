using LinuxRemoteWorker.Cli.Commands;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Cli;

public class TargetResolverTests
{
    private static ConnectionTarget Resolve(ConnectionOptions options, Dictionary<string, string>? env = null,
        List<ConnectionProfile>? profiles = null, string? defaultProfile = null) =>
        TargetResolver.Resolve(options, name => env?.GetValueOrDefault(name), () => profiles ?? [], () => defaultProfile);

    private static ConnectionProfile PasswordProfile(string? protectedPassword = null) => new()
    {
        Name = "prod", Host = "203.0.113.10", Username = "deploy", AuthMethod = SshAuthMethod.Password, ProtectedPassword = protectedPassword
    };

    [Fact]
    public void Host_with_a_key_logs_in_with_the_key()
    {
        var target = Resolve(new ConnectionOptions { Host = "203.0.113.10", Key = "id_ed25519" });

        Assert.Equal(SshAuthMethod.Key, target.Method);
        Assert.Equal(Path.GetFullPath("id_ed25519"), target.KeyPath);
        Assert.Equal("root", target.Username);
    }

    [Fact]
    public void Host_without_a_key_logs_in_with_the_password_variable()
    {
        var target = Resolve(new ConnectionOptions { Host = "203.0.113.10", User = "deploy" },
            new() { [TargetResolver.PasswordVariable] = "pw" });

        Assert.Equal(SshAuthMethod.Password, target.Method);
        Assert.Equal("pw", target.Password);
        Assert.Equal("deploy", target.Username);
    }

    [Fact]
    public void Host_without_key_or_password_explains_both_ways()
    {
        var ex = Assert.Throws<UsageException>(() => Resolve(new ConnectionOptions { Host = "203.0.113.10" }));

        Assert.Contains("--key", ex.Message);
        Assert.Contains(TargetResolver.PasswordVariable, ex.Message);
    }

    [Fact]
    public void A_named_password_variable_wins_and_must_exist()
    {
        var env = new Dictionary<string, string> { ["PROD_PW"] = "from-named", [TargetResolver.PasswordVariable] = "from-default" };

        Assert.Equal("from-named", Resolve(new ConnectionOptions { Host = "h", PasswordEnv = "PROD_PW" }, env).Password);
        Assert.Throws<UsageException>(() => Resolve(new ConnectionOptions { Host = "h", PasswordEnv = "MISSING" }, env));
    }

    [Fact]
    public void A_password_profile_takes_the_password_from_the_environment()
    {
        var target = Resolve(new ConnectionOptions { Profile = "prod" }, new() { [TargetResolver.PasswordVariable] = "pw" },
            [PasswordProfile()]);

        Assert.Equal(SshAuthMethod.Password, target.Method);
        Assert.Equal("pw", target.Password);
        Assert.Equal("prod", target.ProfileName);
    }

    [Fact]
    public void A_password_profile_without_any_password_leaves_it_to_the_prompt()
    {
        var target = Resolve(new ConnectionOptions(), profiles: [PasswordProfile()]);

        Assert.Equal(SshAuthMethod.Password, target.Method);
        Assert.Null(target.Password);
    }

    [Fact]
    public void A_saved_password_is_used_when_nothing_else_is_given()
    {
        if (!PasswordProtector.IsSupported) return;

        var target = Resolve(new ConnectionOptions(), profiles: [PasswordProfile(PasswordProtector.Protect("saved"))]);

        Assert.Equal("saved", target.Password);
    }

    [Fact]
    public void Key_profiles_ignore_passwords()
    {
        var profile = new ConnectionProfile { Name = "k", Host = "h", PrivateKeyPath = @"C:\keys\id" };

        var target = Resolve(new ConnectionOptions(), new() { [TargetResolver.PasswordVariable] = "pw" }, [profile]);

        Assert.Equal(SshAuthMethod.Key, target.Method);
        Assert.Null(target.Password);
    }
}

public class SecurityCommandLineTests
{
    private static readonly CommandCatalog Catalog = CommandCatalog.Build(typeof(SecurityCommands).Assembly);

    private static ParsedCommandLine Parse(params string[] args) => CommandLineParser.Parse(Catalog, args);

    [Fact]
    public void Profiles_take_a_password_from_a_variable_and_never_log_it()
    {
        Environment.SetEnvironmentVariable("LRW_TEST_PROFILE_PW", "hunter2");
        try
        {
            var parsed = Parse("profiles", "add", "prod", "--host", "203.0.113.10", "--password", "env:LRW_TEST_PROFILE_PW");

            Assert.Equal("hunter2", parsed.Values["password"]);
            Assert.DoesNotContain(parsed.Redacted, word => word.Contains("hunter2"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LRW_TEST_PROFILE_PW", null);
        }
    }

    [Fact]
    public void Creating_an_admin_copies_the_current_keys_unless_told_not_to()
    {
        var parsed = Parse("security", "create-admin", "--user", "admin", "--no-copy-keys");

        Assert.Equal("admin", parsed.Values["user"]);
        Assert.Equal(false, parsed.Values["copyKeys"]);
        Assert.True((bool)Catalog.Commands.Single(c => c.FullPath == "security create-admin").Parameters.Single(p => p.Name == "copyKeys").Default!);
    }

    [Fact]
    public void Another_devices_public_key_can_come_from_a_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT test@mac\n");

            var parsed = Parse("security", "add-key", "--public-key", "@" + file, "--user", "admin");

            Assert.StartsWith("ssh-ed25519 AAAA", (string)parsed.Values["publicKey"]!);
            Assert.Equal("admin", parsed.Values["user"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Password_options_are_global()
    {
        var parsed = Parse("connection", "show", "--password-env", "PROD_PW");

        Assert.Equal("PROD_PW", parsed.Globals.Connection.PasswordEnv);
    }

    [Theory]
    [InlineData("security disable-password-login", true)]
    [InlineData("security enable-password-login", true)]
    [InlineData("security disable-root-login", true)]
    [InlineData("security enable-root-login", true)]
    [InlineData("security create-admin", false)]
    [InlineData("security remove-key", true)]
    [InlineData("security add-key", false)]
    [InlineData("security add-my-key", false)]
    [InlineData("connection forget-host-key", true)]
    [InlineData("security status", false)]
    [InlineData("security create-key", false)]
    public void Changes_that_weaken_or_lock_down_logins_need_confirmation(string command, bool destructive)
    {
        Assert.Equal(destructive, Catalog.Commands.Single(c => c.FullPath == command).Destructive);
    }

    [Fact]
    public async Task Disabling_password_login_without_yes_is_refused_before_any_server_is_picked()
    {
        var picked = false;
        using var session = new ServerSession(() =>
        {
            picked = true;
            throw new InvalidOperationException("no server in unit tests");
        });
        var parsed = Parse("security", "disable-password-login");

        var result = await CommandInvoker.InvokeAsync(parsed.Command!, new CommandContext(session, _ => { }, progressVisible: false),
            parsed.Values, CancellationToken.None);

        Assert.Equal(ExitCodes.Refused, result.ExitCode);
        Assert.False(picked);
    }
}
