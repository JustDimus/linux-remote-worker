using System.IO;
using System.Text.Json;

namespace LinuxRemoteWorker.Core;

public class ProfileService
{
    /// <summary>Where saved servers live. Public so the UI can show and open it.</summary>
    public static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<ConnectionProfile> Load()
    {
        if (!File.Exists(ProfilesPath))
            return [];

        try
        {
            var json = File.ReadAllText(ProfilesPath);
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // A damaged profiles file must not stop the app from starting - the
            // user can still type the connection details by hand.
            AppLog.Error($"Cannot read saved profiles from {ProfilesPath}", ex);
            return [];
        }
    }

    /// <summary>Shows profiles.json in Explorer, or the folder when the file does not exist yet.</summary>
    public static void RevealInExplorer()
    {
        try
        {
            var folder = Path.GetDirectoryName(ProfilesPath)!;
            Directory.CreateDirectory(folder);
            var args = File.Exists(ProfilesPath) ? $"/select,\"{ProfilesPath}\"" : $"\"{folder}\"";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Cannot open the profiles folder", ex);
        }
    }

    public void Save(List<ConnectionProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(profiles, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot save profiles to {ProfilesPath}", ex);
            throw;
        }
    }
}
