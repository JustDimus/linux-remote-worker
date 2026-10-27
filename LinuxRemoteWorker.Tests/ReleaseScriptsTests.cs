using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests;

/// <summary>
/// The scripts the Release workflow runs (tools/). They need a bash (see <see cref="TestBash"/>); signing also
/// needs openssl, which the CI runners and Git Bash have.
/// </summary>
public sealed class ReleaseScriptsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lrw-release-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Script(string name) => File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", name));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LinuxRemoteWorker.sln"))) return dir.FullName;
        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    private static string Unix(string path) => path.Replace('\\', '/');

    [Theory]
    [InlineData("0.0.3", "patch", "0.0.4")]
    [InlineData("0.0.9", "patch", "0.0.10")]
    [InlineData("0.4.2", "minor", "0.5.0")]
    [InlineData("0.4.2", "major", "1.0.0")]
    [InlineData("12.34.56", "patch", "12.34.57")]
    public void The_next_version_raises_one_part_and_resets_the_smaller_ones(string current, string bump, string next)
    {
        if (TestBash.Path is not { } bash) return;

        var (exitCode, output, errors) = TestBash.Run(bash, Script("next-version.sh"), "-s", "--", current, bump);

        Assert.True(exitCode == 0, errors);
        Assert.Equal(next, output.Trim());
    }

    [Theory]
    [InlineData("1.2", "patch")]
    [InlineData("v1.2.3", "patch")]
    [InlineData("1.2.3", "build")]
    [InlineData("", "")]
    public void Anything_else_is_refused(string current, string bump)
    {
        if (TestBash.Path is not { } bash) return;

        var (exitCode, _, _) = TestBash.Run(bash, Script("next-version.sh"), "-s", "--", current, bump);

        Assert.Equal(2, exitCode);
    }

    // The workflow signs with openssl; the app verifies with .NET - both must agree on the signed bytes
    [Fact]
    public void A_release_signed_by_the_script_checks_out_in_the_app()
    {
        if (TestBash.Path is not { } bash) return;
        var key = Unix(Path.Combine(_dir, "signing.key"));
        var package = Path.Combine(_dir, "LinuxRemoteWorker-0.0.4-full.nupkg");
        File.WriteAllBytes(package, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))]);

        var keyGen = TestBash.Run(bash, $"openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out '{key}' && openssl pkey -in '{key}' -pubout");
        if (keyGen.ExitCode != 0 && keyGen.Errors.Contains("command not found")) return; // no openssl here
        Assert.True(keyGen.ExitCode == 0, keyGen.Errors);

        var signed = TestBash.Run(bash, Script("sign-update.sh"), "-s", "--", key, "LinuxRemoteWorker", "0.0.4", Unix(package));
        Assert.True(signed.ExitCode == 0, signed.Errors);

        var manifest = UpdateManifest.FromJson(signed.Output);
        Assert.NotNull(manifest);
        Assert.True(manifest.IsSignedBy(keyGen.Output));
        Assert.True(manifest.Describes("LinuxRemoteWorker", "0.0.4", "LinuxRemoteWorker-0.0.4-full.nupkg",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package)))));
    }
}
