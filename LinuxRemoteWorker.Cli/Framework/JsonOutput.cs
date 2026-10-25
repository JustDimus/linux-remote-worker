using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LinuxRemoteWorker.Cli.Framework;

public static class JsonOutput
{
    /// <summary>camelCase properties, kebab-case enums, readable (non-escaped) Unicode such as → and ✓.</summary>
    /// <remarks>
    /// The resolver is set explicitly: JSON nodes holding typed values (JsonValue.Create&lt;T&gt;, JsonArray.Add&lt;T&gt;)
    /// ask the options for type metadata when written, and options that never went through JsonSerializer
    /// have no resolver yet - writing such a node then throws.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>Single-line JSON, for the MCP wire format.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
