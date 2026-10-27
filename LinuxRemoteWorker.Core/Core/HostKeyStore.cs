using System.IO;
using System.Text.Json;

namespace LinuxRemoteWorker.Core;

/// <summary>A server's SSH host key as fingerprinted by `ssh-keygen -lf`: "ssh-ed25519 SHA256:abc…".</summary>
public sealed record HostKey(string Algorithm, string Fingerprint)
{
    public override string ToString() => $"{Algorithm} {Fingerprint}";
}

public sealed record KnownHostKey(string Algorithm, string Fingerprint, DateTime FirstSeenUtc)
{
    public HostKey Key => new(Algorithm, Fingerprint);
}

public enum HostKeyVerdict { Unknown, Trusted, Changed }

/// <summary>
/// The servers this computer has connected to and their host keys (known_hosts.json next to profiles.json,
/// shared by the app and lrw). A server's key is remembered on the first successful login; a different key
/// later is refused, because that is what someone intercepting the connection would present.
/// </summary>
public sealed class HostKeyStore
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "known_hosts.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object FileLock = new();

    public HostKeyStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
    }

    public static HostKeyStore Default { get; } = new();

    public string FilePath { get; }

    public KnownHostKey? Lookup(string host)
    {
        lock (FileLock) return Load().GetValueOrDefault(Normalize(host));
    }

    public HostKeyVerdict Check(string host, HostKey seen)
    {
        var known = Lookup(host);
        if (known == null) return HostKeyVerdict.Unknown;
        return known.Algorithm == seen.Algorithm && known.Fingerprint == seen.Fingerprint
            ? HostKeyVerdict.Trusted
            : HostKeyVerdict.Changed;
    }

    public void Remember(string host, HostKey key)
    {
        lock (FileLock)
        {
            var all = Load();
            all[Normalize(host)] = new KnownHostKey(key.Algorithm, key.Fingerprint, DateTime.UtcNow);
            Save(all);
        }
        AppLog.Info($"Host key of {host} remembered: {key}");
    }

    /// <summary>Forgets a server's key so the next login remembers whatever key it presents. False when none was stored.</summary>
    public bool Forget(string host)
    {
        lock (FileLock)
        {
            var all = Load();
            if (!all.Remove(Normalize(host))) return false;
            Save(all);
        }
        AppLog.Info($"Host key of {host} forgotten");
        return true;
    }

    private static string Normalize(string host) => host.Trim().Trim('[', ']').ToLowerInvariant();

    // A damaged file is an error, not an empty list: treating it as empty would accept any server's key
    private Dictionary<string, KnownHostKey> Load()
    {
        if (!File.Exists(FilePath)) return new Dictionary<string, KnownHostKey>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, KnownHostKey>>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new Dictionary<string, KnownHostKey>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw OperationException.Failed(
                $"{FilePath} cannot be read ({ex.Message}). It holds the SSH keys of known servers; fix it, or delete it " +
                "to remember each server's key again on its next login.");
        }
    }

    private void Save(Dictionary<string, KnownHostKey> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, KnownHostKey>(all), JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>
/// The login worked, but the server closes every session at once - so no command can run. Most often the password
/// has expired and must be changed in a terminal first (new hosting servers), otherwise the account has no shell.
/// </summary>
public sealed class SessionRefusedException : Exception
{
    private SessionRefusedException(string message, string serverText, bool passwordExpired, Exception? inner)
        : base(message, inner)
    {
        ServerText = serverText;
        PasswordExpired = passwordExpired;
    }

    /// <summary>What the server wrote before closing the session (may be empty).</summary>
    public string ServerText { get; }

    /// <summary>The server asked for a new password - it cannot be set without a terminal.</summary>
    public bool PasswordExpired { get; }

    public static SessionRefusedException From(string username, string host, string serverText, string detail, Exception? inner)
    {
        var text = serverText.Trim();
        var expired = LooksExpired(text) || LooksExpired(detail);
        return new SessionRefusedException(
            expired
                ? $"{username}@{host}: the password has expired and must be changed first. {text}".Trim()
                : $"{username}@{host} logged in, but the server closed the session at once ({(text.Length > 0 ? text : detail)}).",
            text, expired, inner);
    }

    // OpenSSH with PAM: "You are required to change your password immediately", "Password change required but no TTY available"
    private static bool LooksExpired(string text) =>
        text.Contains("password has expired", StringComparison.OrdinalIgnoreCase)
        || text.Contains("change your password", StringComparison.OrdinalIgnoreCase)
        || text.Contains("password change required", StringComparison.OrdinalIgnoreCase)
        || text.Contains("password expired", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The server presented a different host key than the one remembered for it.</summary>
public sealed class HostKeyChangedException : Exception
{
    public HostKeyChangedException(string host, KnownHostKey known, HostKey seen)
        : base($"The SSH host key of {host} changed: known {known.Key} (since {known.FirstSeenUtc:yyyy-MM-dd}), now {seen}.")
    {
        Host = host;
        Known = known;
        Seen = seen;
    }

    public string Host { get; }
    public KnownHostKey Known { get; }
    public HostKey Seen { get; }
}
