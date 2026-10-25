using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxRemoteWorker.Cli.Framework;

public static class JsonOutput
{
    /// <summary>camelCase properties, kebab-case enums, readable (non-escaped) Unicode such as → and ✓.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    /// <summary>Single-line JSON, for the MCP wire format.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
