using System.Runtime.CompilerServices;

namespace LinuxRemoteWorker.Tests;

internal static class TestSetup
{
    /// <summary>Keeps what the code under test logs out of the user's real app log.</summary>
    [ModuleInitializer]
    internal static void RedirectLogs() =>
        Environment.SetEnvironmentVariable("LRW_LOG_DIR", Path.Combine(Path.GetTempPath(), "lrw-test-logs"));
}
