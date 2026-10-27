using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("files", "List, read, download and upload files on the server (SFTP)")]
public sealed class FileCommands : CommandSet
{
    private const int MaxLines = 10000;

    public FileCommands(CommandContext context) : base(context) { }

    [Command("list", "List a directory on the server: name, type, size and modification time.", ReadOnly = true)]
    public async Task<object> List([Arg("Absolute directory path on the server", Positional = true, RemotePath = true)] string path)
    {
        var dir = RemotePath(path);
        var ssh = await SshAsync();
        if (!await ssh.ExistsAsync(dir)) throw OperationException.NotFound($"{dir} does not exist on the server.");
        return new { path = dir, entries = await ssh.ListDirectoryDetailedAsync(dir) };
    }

    [Command("read", "Print the last (or with --head the first) lines of a text file on the server.", ReadOnly = true,
        Example = "files read /etc/nginx/nginx.conf --head --lines 100")]
    public async Task<object> Read(
        [Arg("Absolute file path on the server", Positional = true, RemotePath = true)] string path,
        [Arg("How many lines")] int lines = 200,
        [Arg("Read from the start of the file instead of the end")] bool head = false)
    {
        var file = RemotePath(path);
        var count = Math.Clamp(lines, 1, MaxLines);
        var ssh = await SshAsync();
        var r = await ssh.ExecuteAsync(
            $"[ -f {Shell.Quote(file)} ] || {{ echo 'Not a file: {file.Replace("'", "")}'; exit 3; }}; " +
            $"{(head ? "head" : "tail")} -n {count} {Shell.Quote(file)} 2>&1");
        if (r.ExitCode == 3) throw OperationException.NotFound(r.Text);
        if (!r.Succeeded) throw OperationException.Failed(r.Text);
        return new { path = file, lines = count, from = head ? "start" : "end", text = r.Output.TrimEnd() };
    }

    [Command("download", "Copy a file from the server to this computer.", ReadOnly = true,
        Example = "files download /var/log/nginx/error.log --to ./logs/")]
    public async Task<Outcome> Download(
        [Arg("Absolute file path on the server", Positional = true, RemotePath = true)] string path,
        [Arg("Local file or existing folder; default: the current folder")] string? to = null)
    {
        var remote = RemotePath(path);
        var local = LocalTarget(to, remote);
        await (await SshAsync()).DownloadFileAsync(remote, local);
        var bytes = new FileInfo(local).Length;
        return new Outcome($"Downloaded {remote} to {local} ({bytes} bytes)") { Data = new { remote, local, bytes } };
    }

    [Command("upload", "Copy a file from this computer to the server. An existing file is only replaced with --overwrite.",
        Unrestricted = true, Example = "files upload ./appsettings.Production.json /srv/lrw/apps/shop-api/")]
    public async Task<Outcome> Upload(
        [Arg("Local file to send", Positional = true)] string local,
        [Arg("Absolute target path on the server; ending in / means 'into this directory'", Positional = true, RemotePath = true)] string path,
        [Arg("Replace the file if it already exists")] bool overwrite = false)
    {
        var source = Path.GetFullPath(local);
        if (!File.Exists(source)) throw OperationException.NotFound($"Local file not found: {source}");

        var remote = RemotePath(path);
        if (remote.EndsWith('/')) remote += Path.GetFileName(source);

        var ssh = await SshAsync();
        if (!overwrite && await ssh.ExistsAsync(remote))
            throw OperationException.Refused($"{remote} already exists on the server. Pass --overwrite to replace it.");

        await ssh.UploadFileAsync(source, remote, overwrite);
        var bytes = new FileInfo(source).Length;
        return new Outcome($"Uploaded {source} to {remote} ({bytes} bytes)") { Data = new { local = source, remote, bytes } };
    }

    /// <summary>An absolute server path without control characters ("~" and relative paths are ambiguous over SFTP).</summary>
    internal static string RemotePath(string path)
    {
        var p = path.Trim();
        if (!p.StartsWith('/') || p.Any(char.IsControl))
            throw OperationException.Invalid($"'{path}' is not an absolute path on the server (it must start with /).");
        return p;
    }

    internal static string LocalTarget(string? to, string remote)
    {
        var name = remote.TrimEnd('/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(to)) return Path.GetFullPath(name);

        var full = Path.GetFullPath(to);
        if (Directory.Exists(full) || to.EndsWith('/') || to.EndsWith('\\'))
        {
            Directory.CreateDirectory(full);
            return Path.Combine(full, name);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }
}
