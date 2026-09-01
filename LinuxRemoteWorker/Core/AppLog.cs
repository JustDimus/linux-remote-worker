using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LinuxRemoteWorker.Core;

/// <summary>
/// Lightweight thread-safe file logger.
/// Writes to %AppData%\LinuxRemoteWorker\logs\app-yyyy-MM-dd.log
/// Also keeps the most recent lines in memory so the in-app log viewer
/// can show them live without re-reading the file.
/// </summary>
public static class AppLog
{
    private const int MemoryBufferLines = 2000;
    private const int RetentionDays = 14;

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Recent = new();

    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinuxRemoteWorker", "logs");

    /// <summary>Raised for every written line (already formatted).</summary>
    public static event Action<string>? EntryWritten;

    public static string CurrentFile =>
        Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    public static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";

        Recent.Enqueue(line);
        while (Recent.Count > MemoryBufferLines) Recent.TryDequeue(out _);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app
        }

        try { EntryWritten?.Invoke(line); } catch { /* viewer must never break logging */ }
    }

    /// <summary>Lines kept in memory for the current session, oldest first.</summary>
    public static IReadOnlyList<string> RecentLines() => Recent.ToArray();

    /// <summary>Log files in the log directory, newest first.</summary>
    public static IReadOnlyList<string> ListFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return [];
            return new DirectoryInfo(LogDirectory)
                .GetFiles("app-*.log")
                .OrderByDescending(f => f.Name)
                .Select(f => f.Name)
                .ToArray();
        }
        catch (Exception ex)
        {
            Error("Failed to list log files", ex);
            return [];
        }
    }

    public static string PathOf(string fileName) => Path.Combine(LogDirectory, fileName);

    /// <summary>
    /// Reads the last <paramref name="maxLines"/> lines of a log file.
    /// Uses shared read access so it works while the app is writing to it.
    /// </summary>
    public static string ReadTail(string fileName, int maxLines = 2000)
    {
        var path = PathOf(fileName);
        try
        {
            if (!File.Exists(path)) return $"(log file not found: {path})";

            var lines = new Queue<string>(maxLines);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines) lines.Dequeue();
            }

            return lines.Count == 0 ? "(log file is empty)" : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"(cannot read {path}: {ex.Message})";
        }
    }

    /// <summary>Deletes log files older than the retention window.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in new DirectoryInfo(LogDirectory).GetFiles("app-*.log"))
                if (f.LastWriteTime < cutoff)
                    f.Delete();
        }
        catch (Exception ex)
        {
            Error("Log cleanup failed", ex);
        }
    }

    /// <summary>Opens a log file in the default text editor.</summary>
    public static void OpenFile(string? fileName = null)
    {
        var path = fileName == null ? CurrentFile : PathOf(fileName);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(path, string.Empty);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error($"Cannot open log file {path}", ex);
        }
    }

    /// <summary>Opens the log directory in Explorer (selecting the current file when it exists).</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var current = CurrentFile;
            var args = File.Exists(current) ? $"/select,\"{current}\"" : $"\"{LogDirectory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("Cannot open log folder", ex);
        }
    }
}
