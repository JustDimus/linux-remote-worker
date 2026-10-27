using System.Diagnostics;

namespace LinuxRemoteWorker.Tests;

/// <summary>
/// A local bash for checking generated scripts: /bin/bash on Linux and macOS, or the path in LRW_BASH
/// (e.g. Git Bash on Windows). Without one, the tests that need it pass without checking.
/// </summary>
internal static class TestBash
{
    public static string? Path =>
        Environment.GetEnvironmentVariable("LRW_BASH") is { Length: > 0 } configured ? configured
        : !OperatingSystem.IsWindows() && File.Exists("/bin/bash") ? "/bin/bash"
        : null;

    /// <summary>
    /// Runs bash with <paramref name="args"/> (default: -s) and the script on stdin. Each argument is passed
    /// as is - no shell quoting, which Linux would hand over literally while Git Bash strips it.
    /// </summary>
    public static (int ExitCode, string Output, string Errors) Run(string bash, string script, params string[] args)
    {
        var start = new ProcessStartInfo(bash)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args.Length > 0 ? args : ["-s"]) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(script.Replace("\r\n", "\n"));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result, errors);
    }
}
