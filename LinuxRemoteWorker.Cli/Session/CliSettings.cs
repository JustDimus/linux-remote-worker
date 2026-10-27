using System.Text.Json;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Session;

/// <summary>Settings of the command line only (the desktop app does not read them): the default profile.</summary>
public sealed class CliSettings
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "cli.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Profile used when a command passes neither --profile nor --host.</summary>
    public string? DefaultProfile { get; set; }

    /// <summary>When an interactive run last asked GitHub for a newer release (see UpdateNotifier).</summary>
    public DateTime? UpdateCheckedUtc { get; set; }

    public static CliSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<CliSettings>(File.ReadAllText(SettingsPath)) ?? new CliSettings()
                : new CliSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot read CLI settings from {SettingsPath}", ex);
            return new CliSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
