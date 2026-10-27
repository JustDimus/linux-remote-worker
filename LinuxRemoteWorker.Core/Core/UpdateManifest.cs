using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinuxRemoteWorker.Core;

/// <summary>
/// What the Release workflow signs for every desktop-app release: the package, its SHA256 and an ECDSA P-256
/// signature over both, made with the private key kept in the repository's secrets. The app installs an update
/// only when this checks out against the public key built into it - so a release asset swapped by someone without
/// that key is refused, even though it comes from the right place over HTTPS.
/// </summary>
public sealed record UpdateManifest(string PackageId, string Version, string File, string Sha256, string Signature)
{
    /// <summary>The release asset the manifest is published as (tools/sign-update.sh writes it).</summary>
    public const string FileName = "LinuxRemoteWorker-update.sig.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The exact text that is signed - one field per line, the hash in upper case. tools/sign-update.sh builds the
    /// same bytes with printf, so the two must change together.
    /// </summary>
    public static string SignedText(string packageId, string version, string file, string sha256) =>
        $"lrw-update-v1\n{packageId}\n{version}\n{file}\n{sha256.ToUpperInvariant()}\n";

    public string SignedTextOfThis => SignedText(PackageId, Version, File, Sha256);

    /// <summary>True when <paramref name="publicKeyPem"/> made the signature over these fields.</summary>
    public bool IsSignedBy(string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(SignedTextOfThis), Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when the manifest describes exactly this package (the hash compared without regard to case).</summary>
    public bool Describes(string packageId, string version, string file, string sha256) =>
        sha256.Length > 0
        && PackageId == packageId && Version == version && File == file
        && string.Equals(Sha256, sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Signs with a private key - what the Release workflow does with openssl; used by the tests.</summary>
    public static UpdateManifest Sign(string privateKeyPem, string packageId, string version, string file, string sha256)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(Encoding.UTF8.GetBytes(SignedText(packageId, version, file, sha256)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new UpdateManifest(packageId, version, file, sha256.ToUpperInvariant(), Convert.ToBase64String(signature));
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Null when the text is not a manifest.</summary>
    public static UpdateManifest? FromJson(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            return manifest is { PackageId: not null, Version: not null, File: not null, Sha256: not null, Signature: not null }
                ? manifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
