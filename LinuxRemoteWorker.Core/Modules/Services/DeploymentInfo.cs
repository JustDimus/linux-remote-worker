using System.Text.Json;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.Modules.Services;

/// <summary>
/// What a service runs: the project, the clone it was built from, what that clone followed, and the exact
/// version and commit. Written to /srv/lrw/apps/&lt;app&gt;/.lrw-deploy.json on every deploy and redeploy.
/// </summary>
public sealed record DeploymentInfo(
    string Project,
    string? Repository,
    string? Source,
    string? Version,
    string? Commit,
    string Environment,
    string Urls,
    DateTime DeployedAtUtc)
{
    public const string FileName = ".lrw-deploy.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>"v1.4.2 (8b1c0de) from shop-prod · Production · 2026-10-26 14:05 UTC".</summary>
    public string Describe()
    {
        var what = Version == null ? "unknown version" : $"{Version} ({GitRefs.Short(Commit)})";
        var from = Repository == null ? "" : $" from {Repository}";
        return $"{what}{from} · {Environment} · {DeployedAtUtc:yyyy-MM-dd HH:mm} UTC";
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Null when the text is empty or not a deployment record (services deployed by older versions have none).</summary>
    public static DeploymentInfo? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeploymentInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
