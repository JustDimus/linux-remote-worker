using System.Security.Cryptography;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Core;

public class UpdateManifestTests
{
    private const string Sha = "4D5637EECBDC1DB2376D4839104C854ACBB6ADCDC39422F6299957F71A380681";

    private static (string PrivatePem, string PublicPem) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void A_manifest_signed_with_the_key_checks_out()
    {
        var (privatePem, publicPem) = NewKey();

        var manifest = UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "LinuxRemoteWorker-0.0.4-full.nupkg", Sha.ToLowerInvariant());

        Assert.True(manifest.IsSignedBy(publicPem));
        Assert.True(manifest.Describes("LinuxRemoteWorker", "0.0.4", "LinuxRemoteWorker-0.0.4-full.nupkg", Sha));
    }

    [Fact]
    public void Another_key_does_not_check_out()
    {
        var (privatePem, _) = NewKey();
        var (_, otherPublic) = NewKey();

        Assert.False(UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "f.nupkg", Sha).IsSignedBy(otherPublic));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("file")]
    [InlineData("sha")]
    [InlineData("package")]
    public void Changing_any_signed_field_breaks_the_signature(string field)
    {
        var (privatePem, publicPem) = NewKey();
        var manifest = UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "f.nupkg", Sha);

        var tampered = field switch
        {
            "version" => manifest with { Version = "9.9.9" },
            "file" => manifest with { File = "evil.nupkg" },
            "sha" => manifest with { Sha256 = new string('A', 64) },
            _ => manifest with { PackageId = "Other" }
        };

        Assert.False(tampered.IsSignedBy(publicPem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void A_broken_signature_does_not_check_out(string signature)
    {
        var (privatePem, publicPem) = NewKey();
        var manifest = UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "f.nupkg", Sha) with { Signature = signature };

        Assert.False(manifest.IsSignedBy(publicPem));
    }

    [Fact]
    public void A_broken_public_key_does_not_check_out()
    {
        var (privatePem, _) = NewKey();

        Assert.False(UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "f.nupkg", Sha).IsSignedBy("not a key"));
    }

    [Fact]
    public void A_valid_signature_for_another_package_is_still_refused()
    {
        var (privatePem, publicPem) = NewKey();
        var old = UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.3", "LinuxRemoteWorker-0.0.3-full.nupkg", Sha);

        Assert.True(old.IsSignedBy(publicPem));
        Assert.False(old.Describes("LinuxRemoteWorker", "0.0.4", "LinuxRemoteWorker-0.0.4-full.nupkg", Sha));
        Assert.False(old.Describes("LinuxRemoteWorker", "0.0.3", "LinuxRemoteWorker-0.0.3-full.nupkg", ""));
    }

    [Fact]
    public void The_manifest_survives_its_json()
    {
        var (privatePem, publicPem) = NewKey();
        var manifest = UpdateManifest.Sign(privatePem, "LinuxRemoteWorker", "0.0.4", "f.nupkg", Sha);

        var read = UpdateManifest.FromJson(manifest.ToJson());

        Assert.Equal(manifest, read);
        Assert.True(read!.IsSignedBy(publicPem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"packageId\":\"x\"}")]
    public void Anything_else_is_not_a_manifest(string json)
    {
        Assert.Null(UpdateManifest.FromJson(json));
    }
}
