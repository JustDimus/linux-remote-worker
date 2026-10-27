using System.Security.Cryptography;
using System.Text;

namespace LinuxRemoteWorker.Core;

/// <summary>
/// Encrypts saved passwords with Windows DPAPI for the current user: profiles.json then holds a blob that
/// only this Windows account on this computer can turn back into the password. Not available elsewhere.
/// </summary>
public static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LinuxRemoteWorker/profile-password/v1");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string Protect(string password)
    {
        if (!OperatingSystem.IsWindows())
            throw OperationException.Invalid("Passwords can only be saved on Windows. Pass the password when connecting instead.");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>The password, or null when the blob is damaged or belongs to another user or computer.</summary>
    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLog.Warn($"A saved password could not be decrypted: {ex.Message}");
            return null;
        }
    }
}
