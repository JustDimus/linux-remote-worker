using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Tests.Security;

public class SshPublicKeyTests
{
    // Throwaway keys made with ssh-keygen; fingerprints as `ssh-keygen -lf` printed them
    private const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT test@mac";
    private const string Ed25519Fingerprint = "SHA256:OEqfr3cu2y53GE4v20XH/sXXsg8nPNoQQxW2MSC1xjU";

    private const string Ecdsa = "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBCDE1828OYVnyhvFpOAtPVw5P8mAH3Q5JZzEIaRSE7Y7tz1nddSY4HbrUkbk3L4qDFk5m2eFI28ptu8m5gC4kfE= test@pc";
    private const string EcdsaFingerprint = "SHA256:RaZMcXT3nkmOif5prvhFKZRokVwCVYu9Zyq+3cnKs7c";

    private const string Rsa = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCtf76ifYPxHsHtXXszUHgIJG9EzciopVfhSKvz+vSbGiRJEiOv0pz7wH+oIWXrEGDt+JCz+RLp9R9OTLvS+wbXT7quPHqn3l1rTix4MzlqB18r+jjXi6pLXV68YNFCgY25buSWiJ/i65tqjXQosp0r7FyfXf6Q9Oo696jD1KUgeXIZXl0A+DDa9M5XTWCh8N2jfQTY9IHYiu/A5zxDWgNHq1QBWusoIFnNneUVyy7pEk0HTSJuZ0ErKJU1uAYrFed2GVYPTfLgyWjKD5VMGg33e4YMs0iMjROP2ifMN+TOgYU1OanwpR1gcuyudgg7Ef9Tx/RlMzGmb5WVumLma8CR rsa key with spaces";
    private const string RsaFingerprint = "SHA256:x9nJGUfcec6+cBMCumdCY/FvSdfUmOcQvX2w8CPaD5M";

    [Theory]
    [InlineData(Ed25519, "ssh-ed25519", "test@mac", Ed25519Fingerprint)]
    [InlineData(Ecdsa, "ecdsa-sha2-nistp256", "test@pc", EcdsaFingerprint)]
    [InlineData(Rsa, "ssh-rsa", "rsa key with spaces", RsaFingerprint)]
    public void Pub_lines_are_read_and_fingerprinted_like_ssh_keygen(string line, string type, string comment, string fingerprint)
    {
        var key = SshPublicKey.Parse(line);

        Assert.Equal(type, key.Type);
        Assert.Equal(comment, key.Comment);
        Assert.Equal(fingerprint, key.Fingerprint);
        Assert.Equal(line, key.Line);
    }

    [Fact]
    public void Surrounding_whitespace_and_a_missing_comment_are_fine()
    {
        var key = SshPublicKey.Parse("\r\n  ssh-ed25519   AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT  \r\n");

        Assert.Equal("", key.Comment);
        Assert.Equal(Ed25519Fingerprint, key.Fingerprint);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT", key.Line);
    }

    [Fact]
    public void PuTTYgens_SSH2_block_is_read_too()
    {
        var key = SshPublicKey.Parse("""
            ---- BEGIN SSH2 PUBLIC KEY ----
            Comment: "256-bit ED25519, converted by me from OpenSSH"
            AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT
            ---- END SSH2 PUBLIC KEY ----
            """);

        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal(Ed25519Fingerprint, key.Fingerprint);
        Assert.Equal("256-bit ED25519, converted by me from OpenSSH", key.Comment);
    }

    [Theory]
    [InlineData("", "Paste a public key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----", "PRIVATE key")]
    [InlineData(Ed25519 + "\n" + Ecdsa, "one key at a time")]
    [InlineData("from=\"10.0.0.1\" " + Ed25519, "Options before the key")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP", "DSA keys are obsolete")]
    [InlineData("hello world", "Not an SSH public key")]
    [InlineData("ssh-ed25519", "key data is missing")]
    [InlineData("ssh-ed25519 not*base64", "not base64")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT", "does not match its type")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZG", "does not match its type")]
    public void Anything_but_one_public_key_is_refused_with_a_reason(string text, string reason)
    {
        var ex = Assert.Throws<OperationException>(() => SshPublicKey.Parse(text));

        Assert.Equal(FailureKind.Invalid, ex.Kind);
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData(Ed25519)]
    [InlineData("command=\"echo hi there\",no-pty " + Ed25519)]
    [InlineData("  from=\"10.0.0.0/8\"  " + Ed25519 + "  ")]
    public void Authorized_keys_lines_are_read_with_their_options(string line)
    {
        Assert.Equal(Ed25519Fingerprint, SshPublicKey.FromAuthorizedKeysLine(line)?.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT")]
    [InlineData("garbage")]
    [InlineData("ssh-ed25519 broken")]
    public void Other_authorized_keys_lines_are_not_keys(string line)
    {
        Assert.Null(SshPublicKey.FromAuthorizedKeysLine(line));
    }

    [Fact]
    public void Removing_a_key_keeps_every_other_line_as_it_was()
    {
        var content = string.Join("\n",
            "# laptop and mac",
            Rsa,
            "command=\"echo hi there\" " + Ed25519,
            Ecdsa,
            Ed25519 + " second copy",
            "");

        var (result, removed) = SshPublicKey.RemoveFromAuthorizedKeys(content, Ed25519Fingerprint);

        Assert.Equal(2, removed);
        Assert.Equal($"# laptop and mac\n{Rsa}\n{Ecdsa}\n", result);
    }

    [Fact]
    public void Removing_an_unknown_key_changes_nothing()
    {
        var (result, removed) = SshPublicKey.RemoveFromAuthorizedKeys($"{Rsa}\r\n{Ecdsa}\r\n", Ed25519Fingerprint);

        Assert.Equal(0, removed);
        Assert.Equal($"{Rsa}\n{Ecdsa}\n", result);
    }

    [Fact]
    public void The_key_type_is_read_from_the_blob()
    {
        Assert.Equal("ssh-ed25519", SshPublicKey.BlobType(Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT")));
        Assert.Null(SshPublicKey.BlobType([0, 0]));
        Assert.Null(SshPublicKey.BlobType([0xFF, 0xFF, 0xFF, 0xFF, 1, 2]));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("Ubuntu.User", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    public void Existing_user_names_are_checked_loosely(string name, bool valid)
    {
        Assert.Equal(valid, SecurityService.ValidateExistingUserName(name) == null);
    }
}

public sealed class LocalKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lrw-localkey-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_public_half_of_an_ecdsa_private_key_file_is_derived()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = Write("ec_key", ecdsa.ExportPkcs8PrivateKeyPem());
        var q = ecdsa.ExportParameters(false).Q;

        var key = SecurityService.ReadLocalPublicKey(path, null);

        Assert.Equal("ecdsa-sha2-nistp256", key.Type);
        Assert.Equal(Convert.ToBase64String(Blob("ecdsa-sha2-nistp256", Encoding.ASCII.GetBytes("nistp256"), [0x04, .. q.X!, .. q.Y!])), key.Base64);
    }

    [Fact]
    public void The_public_half_of_an_rsa_private_key_file_is_derived()
    {
        using var rsa = RSA.Create(2048);
        var path = Write("rsa_key", rsa.ExportPkcs8PrivateKeyPem());

        var key = SecurityService.ReadLocalPublicKey(path, null);

        Assert.Equal("ssh-rsa", key.Type);
        Assert.Equal("ssh-rsa", SshPublicKey.BlobType(Convert.FromBase64String(key.Base64)));
    }

    [Fact]
    public void The_comment_comes_from_the_matching_pub_file()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = Write("with_pub", ecdsa.ExportPkcs8PrivateKeyPem());
        var derived = SecurityService.ReadLocalPublicKey(path, null);
        File.WriteAllText(path + ".pub", $"{derived.Type} {derived.Base64} me@laptop\n");

        Assert.Equal("me@laptop", SecurityService.ReadLocalPublicKey(path, null).Comment);
    }

    [Fact]
    public void A_pub_file_or_a_missing_file_is_refused()
    {
        var pub = Write("only.pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMZGGpJ09N9iy1YzlROIKzat5we3Xl7XUTWbRdFTYIkT x");

        Assert.Contains("public key", Assert.Throws<OperationException>(() => SecurityService.ReadLocalPublicKey(pub, null)).Message);
        Assert.Contains("not found", Assert.Throws<OperationException>(() => SecurityService.ReadLocalPublicKey(Path.Combine(_dir, "nope"), null)).Message);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static byte[] Blob(string type, params byte[][] fields)
    {
        var parts = new List<byte[]> { Encoding.ASCII.GetBytes(type) };
        parts.AddRange(fields);
        var blob = new List<byte>();
        foreach (var part in parts)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)part.Length);
            blob.AddRange(length);
            blob.AddRange(part);
        }
        return [.. blob];
    }
}
