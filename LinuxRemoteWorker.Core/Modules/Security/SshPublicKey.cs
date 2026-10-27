using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Security;

/// <summary>
/// An OpenSSH public key - the one line of a .pub file: "ssh-ed25519 AAAA… comment". The fingerprint is
/// computed like `ssh-keygen -l` does, so it can be compared with what the other device shows.
/// </summary>
public sealed record SshPublicKey(string Type, string Base64, string Comment)
{
    /// <summary>Key types sshd accepts today (DSA is gone from OpenSSH).</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com"
    ];

    public string Line => Comment.Length == 0 ? $"{Type} {Base64}" : $"{Type} {Base64} {Comment}";

    /// <summary>"SHA256:…" - the same text `ssh-keygen -lf` prints.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Base64))).TrimEnd('=');

    /// <summary>A key from its wire-format blob (as SSH.NET reads it from a private key file).</summary>
    public static SshPublicKey FromBlob(byte[] blob, string comment) =>
        new(BlobType(blob) ?? throw OperationException.Invalid("That is not an SSH public key."), Convert.ToBase64String(blob), comment);

    /// <summary>
    /// Reads a pasted public key: the line of an OpenSSH .pub file, or the "---- BEGIN SSH2 PUBLIC KEY ----" block
    /// PuTTYgen exports. Refuses private keys, several keys, authorized_keys options, unknown types and damaged data.
    /// </summary>
    public static SshPublicKey Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw OperationException.Invalid("Paste a public key - the contents of the .pub file, e.g. ssh-ed25519 AAAA… user@mac.");
        if (trimmed.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw OperationException.Invalid(
                "That is a PRIVATE key - never paste or send it anywhere. Paste the public key instead: the .pub file next to it " +
                "(`cat ~/.ssh/id_ed25519.pub`).");
        if (trimmed.StartsWith("---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal))
            return ParseRfc4716(trimmed);

        var lines = trimmed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
            throw OperationException.Invalid("Paste one key at a time - a public key is a single line.");

        var parts = lines[0].Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (!Types.Contains(parts[0]))
        {
            if (parts[0] == "ssh-dss")
                throw OperationException.Invalid("DSA keys are obsolete and refused by current OpenSSH. Create an Ed25519 key: ssh-keygen -t ed25519");
            if (parts.Skip(1).Any(Types.Contains) || parts[0].Contains('='))
                throw OperationException.Invalid("Options before the key (command=, from=…) are not supported here - paste the plain .pub line.");
            throw OperationException.Invalid($"Not an SSH public key: it should start with one of {string.Join(", ", Types.Take(5))}.");
        }
        if (parts.Length < 2)
            throw OperationException.Invalid("The key data is missing - copy the whole line.");

        var comment = parts.Length == 3 ? parts[2].Trim() : "";
        if (comment.Any(char.IsControl))
            throw OperationException.Invalid("The key's comment contains control characters.");
        return Checked(parts[0], parts[1], comment);
    }

    /// <summary>
    /// One authorized_keys line, options (which may hold quoted spaces) included. Null for blank lines,
    /// comments and anything that is not a readable key.
    /// </summary>
    public static SshPublicKey? FromAuthorizedKeysLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;

        var tokens = Tokens(trimmed);
        var at = tokens.FindIndex(t => Types.Contains(t));
        if (at < 0 || at + 1 >= tokens.Count) return null;
        try
        {
            return Checked(tokens[at], tokens[at + 1], string.Join(' ', tokens.Skip(at + 2)));
        }
        catch (OperationException)
        {
            return null;
        }
    }

    /// <summary>The authorized_keys text without the lines of the key with <paramref name="fingerprint"/>; everything else stays as it was.</summary>
    public static (string Content, int Removed) RemoveFromAuthorizedKeys(string content, string fingerprint)
    {
        var kept = new List<string>();
        var removed = 0;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (FromAuthorizedKeysLine(line)?.Fingerprint == fingerprint) removed++;
            else kept.Add(line);
        }
        return (string.Join('\n', kept).TrimEnd('\n') + "\n", removed);
    }

    /// <summary>The key type written at the start of a wire-format blob, or null when it is not one.</summary>
    public static string? BlobType(byte[] blob)
    {
        if (blob.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length == 0 || length > 64 || 4 + length > blob.Length) return null;
        var type = Encoding.ASCII.GetString(blob, 4, (int)length);
        return type.All(c => c > ' ' && c < 127) ? type : null;
    }

    private static SshPublicKey Checked(string type, string base64, string comment)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw OperationException.Invalid("The key data is damaged (not base64) - copy the whole line again.");
        }
        if (BlobType(blob) != type || !WellFormed(type, blob))
            throw OperationException.Invalid("The key data does not match its type - the line may be cut off or mixed up.");
        return new SshPublicKey(type, base64, comment);
    }

    // The blob is a run of length-prefixed fields; each key type has a fixed number, some of fixed size
    private static bool WellFormed(string type, byte[] blob)
    {
        var fields = new List<int>();
        for (var at = 0; at < blob.Length;)
        {
            if (at + 4 > blob.Length) return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
            if (length > blob.Length - at - 4) return false;
            fields.Add((int)length);
            at += 4 + (int)length;
        }

        return type switch
        {
            "ssh-ed25519" => fields is [_, 32],
            "sk-ssh-ed25519@openssh.com" => fields is [_, 32, > 0],
            "ssh-rsa" => fields is [_, > 0, >= 128],
            "ecdsa-sha2-nistp256" => fields is [_, 8, 65],
            "ecdsa-sha2-nistp384" => fields is [_, 8, 97],
            "ecdsa-sha2-nistp521" => fields is [_, 8, 133],
            "sk-ecdsa-sha2-nistp256@openssh.com" => fields is [_, 8, 65, > 0],
            _ => false
        };
    }

    // RFC 4716: header lines "Name: value" (continued with a trailing backslash), then the base64 body
    private static SshPublicKey ParseRfc4716(string text)
    {
        var body = new StringBuilder();
        var comment = "";
        var inHeader = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("---- ", StringComparison.Ordinal)) continue;
            if (inHeader || line.Contains(':'))
            {
                if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase)) comment = line[8..].Trim().Trim('"');
                inHeader = line.EndsWith('\\');
                continue;
            }
            body.Append(line);
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(body.ToString());
        }
        catch (FormatException)
        {
            throw OperationException.Invalid("The SSH2 public key block is damaged - copy it again.");
        }
        var type = BlobType(blob);
        if (type == null || !Types.Contains(type))
            throw OperationException.Invalid("The SSH2 block does not hold a supported public key.");
        return new SshPublicKey(type, Convert.ToBase64String(blob), comment);
    }

    // Splits on whitespace outside double quotes, so command="a b" stays one token
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
