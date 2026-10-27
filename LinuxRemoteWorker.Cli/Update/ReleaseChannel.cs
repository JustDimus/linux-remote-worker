using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Cli.Update;

/// <summary>
/// The published releases on GitHub. The newest version comes from version.txt through releases/latest/download/,
/// GitHub's permanent link to a file of the newest release; release files are checked against the release's
/// SHA256SUMS.txt before they are used.
/// </summary>
public sealed partial class ReleaseChannel
{
    public const string Repository = "JustDimus/linux-remote-worker";

    private readonly HttpClient _http;

    public ReleaseChannel(HttpClient http)
    {
        _http = http;
    }

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"lrw/{HelpWriter.Version}");
        return client;
    }

    public async Task<Version> LatestVersionAsync(CancellationToken ct)
    {
        var text = await _http.GetStringAsync($"https://github.com/{Repository}/releases/latest/download/version.txt", ct);
        return ParseVersion(text) ?? throw OperationException.Failed("The newest release has no readable version.txt.");
    }

    /// <summary>Saves a file of the release in the folder once it matches SHA256SUMS.txt; returns its path.</summary>
    public async Task<string> DownloadAsync(Version version, string file, string folder, CancellationToken ct)
    {
        var tag = Tag(version);
        var sums = await _http.GetStringAsync(FileUrl(tag, "SHA256SUMS.txt"), ct);
        var expected = ExpectedHash(sums, file) ?? throw OperationException.Failed($"{file} is not listed in SHA256SUMS.txt of {tag}.");
        var content = await _http.GetByteArrayAsync(FileUrl(tag, file), ct);
        if (!Convert.ToHexString(SHA256.HashData(content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw OperationException.Failed($"{file} of {tag} does not match SHA256SUMS.txt - it was not used.");

        var path = Path.Combine(folder, file);
        await File.WriteAllBytesAsync(path, content, ct);
        return path;
    }

    public static string Tag(Version version) => $"v{version.ToString(3)}";

    /// <summary>"0.2.1", "v0.2.1" or the content of version.txt; null for anything else.</summary>
    public static Version? ParseVersion(string? text)
    {
        var match = VersionPattern().Match(text?.Trim() ?? "");
        return match.Success && Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version) ? version : null;
    }

    /// <summary>The hash SHA256SUMS.txt (sha256sum output) lists for the file, or null.</summary>
    public static string? ExpectedHash(string sums, string file)
    {
        foreach (var line in sums.Split('\n'))
        {
            var match = SumLine().Match(line.TrimEnd('\r'));
            if (match.Success && match.Groups[2].Value.Trim() == file) return match.Groups[1].Value;
        }
        return null;
    }

    private static string FileUrl(string tag, string file) => $"https://github.com/{Repository}/releases/download/{tag}/{file}";

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex SumLine();
}
