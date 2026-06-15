using System.IO;

namespace LinuxRemoteWorker.Core;

/// <summary>
/// Lightweight thread-safe file logger.
/// Writes to %AppData%\LinuxRemoteWorker\logs\app-yyyy-MM-dd.log
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "logs");

    public static string CurrentFile =>
        Path.Combine(LogDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line);
            }
        }
        catch
        {
            // Logging must never crash the app
        }
    }
}
