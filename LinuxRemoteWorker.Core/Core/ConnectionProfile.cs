using System.Text.Json.Serialization;

namespace LinuxRemoteWorker.Core;

public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = "root";
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Profiles saved before passwords were supported have none and log in with their key.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Key;

    /// <summary>The password encrypted by <see cref="PasswordProtector"/>; null when it is asked for on each login.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public bool HasSavedPassword => !string.IsNullOrEmpty(ProtectedPassword);

    /// <summary>Switches the profile to key login; a saved password has no use after that and is dropped.</summary>
    public void UseKey(string keyPath)
    {
        AuthMethod = SshAuthMethod.Key;
        PrivateKeyPath = keyPath;
        ProtectedPassword = null;
    }

    /// <summary>Switches the profile to password login, saving the password encrypted when one is given.</summary>
    public void UsePassword(string? passwordToSave)
    {
        AuthMethod = SshAuthMethod.Password;
        ProtectedPassword = string.IsNullOrEmpty(passwordToSave) ? null : PasswordProtector.Protect(passwordToSave);
    }
}
