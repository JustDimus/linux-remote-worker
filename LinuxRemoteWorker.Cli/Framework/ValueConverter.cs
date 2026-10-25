using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>Turns option text (CLI) or JSON values (MCP) into the CLR values a command method takes.</summary>
public static class ValueConverter
{
    /// <param name="indirect">Resolve @file / @- / env:NAME (CLI only).</param>
    public static object? FromText(ParameterSpec p, string raw, bool indirect)
    {
        switch (p.Kind)
        {
            case ParamKind.Int:
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw Invalid(p, $"expects a whole number, got '{raw}'.");

            case ParamKind.Bool:
                return ParseBool(raw) ?? throw Invalid(p, $"expects true or false, got '{raw}'.");

            case ParamKind.Enum:
                return CommandCatalog.ParseEnum(p.Type, raw)
                       ?? throw Invalid(p, $"expects one of {string.Join(", ", p.Choices)}, got '{raw}'.");

            default:
                var text = indirect ? ResolveIndirect(p, raw) : raw;
                if (p.Choices.Count == 0) return text;
                return p.Choices.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase))
                       ?? throw Invalid(p, $"expects one of {string.Join(", ", p.Choices)}, got '{text}'.");
        }
    }

    public static object? FromJson(ParameterSpec p, JsonNode? node)
    {
        if (node == null) return p.Required ? throw Invalid(p, "is required.") : p.Default;

        if (node is not JsonValue value)
            throw Invalid(p, $"expects a {p.TypeName}, got {node.GetValueKind().ToString().ToLowerInvariant()}.");

        return value.GetValueKind() switch
        {
            JsonValueKind.String => FromText(p, value.GetValue<string>(), indirect: false),
            JsonValueKind.True or JsonValueKind.False when p.Kind == ParamKind.Bool => value.GetValue<bool>(),
            JsonValueKind.Number when p.Kind == ParamKind.Int && value.TryGetValue<int>(out var i) => i,
            JsonValueKind.Number when p.Kind == ParamKind.String => value.ToJsonString(),
            _ => throw Invalid(p, $"expects a {p.TypeName}, got {value.ToJsonString()}.")
        };
    }

    public static bool? ParseBool(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => null
    };

    // "@path" reads a file, "@-" reads stdin; secrets also take "env:NAME"
    private static string ResolveIndirect(ParameterSpec p, string raw)
    {
        if (p.Secret && raw.StartsWith("env:", StringComparison.Ordinal))
        {
            var name = raw[4..];
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw Invalid(p, $"environment variable {name} is not set.");
        }

        if ((p.AllowFile || p.Secret) && raw.StartsWith('@') && raw.Length > 1)
        {
            var path = raw[1..];
            if (path == "-") return Console.In.ReadToEnd();
            if (!File.Exists(path)) throw Invalid(p, $"file not found: {path}");
            var text = File.ReadAllText(path);
            // a secret file usually ends with a newline that is not part of the secret
            return p.Secret ? text.TrimEnd('\r', '\n') : text;
        }

        return raw;
    }

    private static UsageException Invalid(ParameterSpec p, string problem) =>
        new($"--{p.CliName} {problem}");
}
