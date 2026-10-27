namespace LinuxRemoteWorker.Core;

public enum SshAuthMethod { Key, Password }

/// <summary>
/// Who logs in where, and how: with a private key (optionally encrypted with a passphrase) or with a
/// password. Secrets stay in memory; <see cref="ToString"/> never shows them.
/// </summary>
public sealed record SshCredentials(
    string Host,
    string Username,
    SshAuthMethod Method,
    string KeyPath = "",
    string? Passphrase = null,
    string? Password = null)
{
    public static SshCredentials WithKey(string host, string username, string keyPath, string? passphrase = null) =>
        new(host, username, SshAuthMethod.Key, keyPath, string.IsNullOrEmpty(passphrase) ? null : passphrase);

    public static SshCredentials WithPassword(string host, string username, string? password) =>
        new(host, username, SshAuthMethod.Password, Password: password);

    /// <summary>"key C:\keys\id_ed25519" or "password" - for logs and error reports.</summary>
    public string AuthDescription => Method == SshAuthMethod.Key ? $"key {KeyPath}" : "password";

    // The generated record ToString would print the passphrase and the password
    public override string ToString() => $"{Username}@{Host} ({AuthDescription})";
}
