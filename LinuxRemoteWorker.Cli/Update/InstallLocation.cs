namespace LinuxRemoteWorker.Cli.Update;

/// <summary>Where scripts/install-cli.* put the program, and whether this process runs from there.</summary>
public static class InstallLocation
{
    // The same folders the scripts use: %LOCALAPPDATA% on Windows (the variable, as install-cli.ps1 reads it), $HOME elsewhere
    public static string StandardPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local
            ? local
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "lrw", "lrw.exe")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "lrw", "lrw");

    /// <summary>The running executable with links resolved - started through ~/.local/bin/lrw, it is the program itself.</summary>
    public static string? CurrentPath => Environment.ProcessPath is { } path ? ResolveLinks(path) : null;

    public static bool IsStandard(string? current, string standard) =>
        current != null && string.Equals(Path.GetFullPath(current), Path.GetFullPath(standard),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On Windows an update renames an lrw.exe that is still running to lrw.exe.&lt;time&gt;.old; the installed lrw
    /// deletes such copies whenever it starts, and one that still runs stays until a later start.
    /// </summary>
    public static void RemoveReplacedCopies()
    {
        if (!OperatingSystem.IsWindows() || CurrentPath is not { } exe || !IsStandard(exe, StandardPath)) return;
        RemoveReplacedCopies(exe);
    }

    internal static void RemoveReplacedCopies(string exe)
    {
        try
        {
            foreach (var copy in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".*.old"))
            {
                try { File.Delete(copy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
