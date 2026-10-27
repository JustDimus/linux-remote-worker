using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Tests.Security;

/// <summary>
/// Runs the authorized_keys scripts on a temporary local file (see <see cref="TestBash"/> for which bash).
/// The file exists beforehand, so only the appending and removing parts run - no chown, no install.
/// </summary>
public sealed class AuthorizedKeysScriptTests : IDisposable
{
    private const string Existing = "ssh-rsa AAAAold admin@laptop";
    private const string NewKey = "ssh-ed25519 AAAAnew lrw-me@PC-20261027";

    private readonly string _dir = Directory.CreateTempSubdirectory("lrw-keys-").FullName;
    private string File => Path.Combine(_dir, "authorized_keys");
    private string BashPath => File.Replace('\\', '/');

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_key_is_appended_on_its_own_line_even_after_a_line_without_newline()
    {
        if (TestBash.Path is not { } bash) return;
        System.IO.File.WriteAllText(File, Existing); // no trailing newline

        var (exitCode, _, errors) = TestBash.Run(bash, SshdConfig.AuthorizeScript(BashPath, "nobody", NewKey));

        Assert.True(exitCode == 0, errors);
        Assert.Equal($"{Existing}\n{NewKey}\n", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Rolling_back_removes_exactly_that_key()
    {
        if (TestBash.Path is not { } bash) return;
        System.IO.File.WriteAllText(File, $"{Existing}\n{NewKey}\n{NewKey} with another comment\n");

        var (exitCode, _, errors) = TestBash.Run(bash, SshdConfig.UnauthorizeScript(BashPath, NewKey));

        Assert.True(exitCode == 0, errors);
        Assert.Equal($"{Existing}\n{NewKey} with another comment\n", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Rolling_back_the_only_key_leaves_an_empty_file()
    {
        if (TestBash.Path is not { } bash) return;
        System.IO.File.WriteAllText(File, $"{NewKey}\n");

        TestBash.Run(bash, SshdConfig.UnauthorizeScript(BashPath, NewKey));

        Assert.True(System.IO.File.Exists(File));
        Assert.Equal("", System.IO.File.ReadAllText(File));
    }
}
