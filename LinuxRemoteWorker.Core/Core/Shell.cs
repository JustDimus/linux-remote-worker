namespace LinuxRemoteWorker.Core;

/// <summary>Builds shell fragments from values that must reach the server literally.</summary>
public static class Shell
{
    private const string HeredocDelimiter = "LRW_FILE_EOF";

    /// <summary>
    /// Non-interactive apt install that waits up to two minutes for a running apt/unattended-upgrades
    /// to release the dpkg lock instead of failing at once.
    /// </summary>
    public static string AptInstall(params string[] packages) =>
        "export DEBIAN_FRONTEND=noninteractive; " +
        "apt-get -o DPkg::Lock::Timeout=120 update -y 2>&1 && " +
        $"apt-get -o DPkg::Lock::Timeout=120 install -y {string.Join(' ', packages)} 2>&1";

    /// <summary>Single-quotes a value so the shell passes it through untouched.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// A command that writes <paramref name="content"/> to <paramref name="path"/> verbatim.
    /// The quoted heredoc disables $variable, `command` and backslash expansion, so nginx
    /// variables like $host survive. Line endings are normalised to LF.
    /// </summary>
    public static string WriteFile(string path, string content) => Heredoc($"cat > {Quote(path)}", content);

    /// <summary>
    /// <paramref name="command"/> with <paramref name="content"/> on its stdin, verbatim (quoted heredoc,
    /// LF line endings). The content never passes through the shell's expansion, so it can safely carry
    /// SQL, passwords or config text.
    /// </summary>
    public static string Heredoc(string command, string content)
    {
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        if (text.Split('\n').Contains(HeredocDelimiter))
            throw new ArgumentException($"The text contains a line '{HeredocDelimiter}', which cannot be passed safely.");

        return $"{command} << '{HeredocDelimiter}'\n{text}\n{HeredocDelimiter}";
    }
}
