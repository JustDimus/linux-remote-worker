using System.Globalization;

namespace LinuxRemoteWorker.Core;

/// <summary>A directory entry on the server, as listed over SFTP or by <c>find -printf</c>.</summary>
public sealed record RemoteFileInfo(
    string Name,
    string FullName,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc);

/// <summary>A directory listing by <c>find -printf</c>, for logins that read the server through sudo instead of SFTP.</summary>
public static class RemoteListing
{
    /// <summary>One line per entry: type (d, f, l…), size, modification time as Unix seconds, name.</summary>
    public static string Command(string directory) =>
        $"find {Shell.Quote(directory)} -mindepth 1 -maxdepth 1 -printf '%y\\t%s\\t%T@\\t%f\\n'";

    public static List<RemoteFileInfo> Parse(string directory, string output)
    {
        var parent = directory.TrimEnd('/');
        var entries = new List<RemoteFileInfo>();
        foreach (var line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 4);
            if (parts.Length != 4 || parts[3].Length == 0
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                continue;
            entries.Add(new RemoteFileInfo(parts[3], $"{parent}/{parts[3]}", parts[0] == "d", size,
                DateTime.UnixEpoch.AddSeconds(seconds)));
        }
        return entries;
    }
}
