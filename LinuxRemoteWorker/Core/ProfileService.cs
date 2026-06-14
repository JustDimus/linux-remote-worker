using System.IO;
using System.Text.Json;

namespace LinuxRemoteWorker.Core;

public class ProfileService
{
    private static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<ConnectionProfile> Load()
    {
        if (!File.Exists(ProfilesPath))
            return [];

        var json = File.ReadAllText(ProfilesPath);
        return JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? [];
    }

    public void Save(List<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
        File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(profiles, JsonOptions));
    }
}
