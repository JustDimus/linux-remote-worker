using System.Text.Json.Nodes;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>
/// Human-readable rendering of a result envelope: lists of records become tables, objects become
/// "key: value" lines, multi-line text is printed as it is. JSON (--json) carries the same data.
/// </summary>
public static class TextRenderer
{
    private const int MaxCell = 100;

    /// <param name="outputShown">The live output already went to the terminal, so it is not repeated.</param>
    public static void Write(JsonObject envelope, TextWriter stdout, TextWriter stderr, bool outputShown)
    {
        if (envelope["ok"]?.GetValue<bool>() == true)
        {
            if (envelope["data"] is { } data)
                RenderValue(data, stdout, 0);
            if (!outputShown && envelope["output"] is JsonValue output)
            {
                stdout.WriteLine();
                stdout.WriteLine(output.GetValue<string>());
            }
            if (envelope["warning"] is JsonValue warning)
                stderr.WriteLine($"warning: {warning.GetValue<string>()}");
            if (envelope["message"] is JsonValue message)
                stdout.WriteLine(message.GetValue<string>());
            return;
        }

        if (envelope["error"] is not JsonObject error) return;
        if (!outputShown && error["output"] is JsonValue errorOutput)
            stderr.WriteLine(errorOutput.GetValue<string>());
        stderr.WriteLine($"error: {Text(error["message"])}");
        if (error["detail"] is JsonValue detail) stderr.WriteLine($"  detail: {detail.GetValue<string>()}");
        if (error["hint"] is JsonValue hint) stderr.WriteLine($"  hint:   {hint.GetValue<string>()}");
    }

    private static void RenderValue(JsonNode node, TextWriter w, int indent)
    {
        switch (node)
        {
            case JsonArray array:
                RenderArray(array, w, indent);
                break;
            case JsonObject obj:
                RenderObject(obj, w, indent);
                break;
            default:
                WriteIndented(w, Text(node), indent);
                break;
        }
    }

    private static void RenderObject(JsonObject obj, TextWriter w, int indent)
    {
        var pad = new string(' ', indent);
        var width = obj.Select(p => p.Key.Length).DefaultIfEmpty(0).Max();
        foreach (var (key, value) in obj)
        {
            switch (value)
            {
                case null:
                    w.WriteLine($"{pad}{key.PadRight(width)}  -");
                    break;
                case JsonArray array when array.All(IsScalar):
                    w.WriteLine($"{pad}{key.PadRight(width)}  {(array.Count == 0 ? "-" : string.Join(", ", array.Select(Text)))}");
                    break;
                case JsonArray or JsonObject:
                    w.WriteLine($"{pad}{key}:");
                    RenderValue(value, w, indent + 2);
                    break;
                default:
                    var text = Text(value);
                    if (text.Contains('\n'))
                    {
                        w.WriteLine($"{pad}{key}:");
                        WriteIndented(w, text, indent + 2);
                    }
                    else
                    {
                        w.WriteLine($"{pad}{key.PadRight(width)}  {text}");
                    }
                    break;
            }
        }
    }

    private static void RenderArray(JsonArray array, TextWriter w, int indent)
    {
        var pad = new string(' ', indent);
        if (array.Count == 0)
        {
            w.WriteLine($"{pad}(none)");
            return;
        }

        if (array.All(IsScalar))
        {
            foreach (var item in array) w.WriteLine($"{pad}{Text(item)}");
            return;
        }

        if (!array.All(n => n is JsonObject))
        {
            foreach (var item in array) RenderValue(item!, w, indent);
            return;
        }

        // A table of the scalar (or scalar-list) columns; nested structures are left to --json
        var rows = array.Cast<JsonObject>().ToList();
        var columns = rows.SelectMany(r => r.Where(p => IsScalar(p.Value) || p.Value is JsonArray a && a.All(IsScalar)).Select(p => p.Key))
            .Distinct().ToList();
        var cells = rows.Select(r => columns.Select(c => Cell(r[c])).ToList()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Max(row => row[i].Length))).ToList();

        w.WriteLine(pad + string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        foreach (var row in cells)
            w.WriteLine(pad + string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private static string Cell(JsonNode? node)
    {
        var text = node is JsonArray a ? string.Join(", ", a.Select(Text)) : Text(node);
        text = text.Replace('\n', ' ');
        return text.Length <= MaxCell ? text : text[..(MaxCell - 1)] + "…";
    }

    private static bool IsScalar(JsonNode? node) => node is null or JsonValue;

    private static string Text(JsonNode? node) => node switch
    {
        null => "-",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "yes" : "no",
        _ => node.ToJsonString()
    };

    private static void WriteIndented(TextWriter w, string text, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            w.WriteLine(pad + line);
    }
}
