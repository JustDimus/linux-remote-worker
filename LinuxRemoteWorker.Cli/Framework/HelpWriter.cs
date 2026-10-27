using System.Text.Json.Nodes;

namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>Help text and the machine-readable schema, both generated from the catalog.</summary>
public static class HelpWriter
{
    public const string GlobalOptionsText =
        """
        Global options (anywhere on the line):
          --profile <name>          Saved server to use. Default: LRW_PROFILE, then `lrw profiles use`,
                                    then the only saved profile
          --host <ip> --key <path>  Connect without a profile (LRW_HOST, LRW_KEY); --user <name> (LRW_USER, root);
                                    without --key the password is used (LRW_PASSWORD or the options below)
          --passphrase-env <VAR>    Variable holding the key passphrase (default LRW_PASSPHRASE)
          --passphrase-file <path>  File holding the key passphrase
          --password-env <VAR>      Variable holding the login password (default LRW_PASSWORD)
          --password-file <path>    File holding the login password
          -o, --output json|text    Default: text on a terminal, json when stdout is redirected
          --json                    Same as --output json
          -q, --quiet               Do not stream remote output (installs, builds) to stderr
          -h, --help                Help for whatever comes before it
        """;

    public static void WriteRoot(CommandCatalog catalog, TextWriter w)
    {
        w.WriteLine($"lrw {Version} - Linux Remote Worker on the command line");
        w.WriteLine();
        w.WriteLine("Usage: lrw [global options] <group> <command> [arguments] [options]");
        w.WriteLine();
        w.WriteLine("Groups:");
        var groups = catalog.Groups.Where(g => g.Commands.Any(c => c.On(Surface.Cli))).ToList();
        var width = groups.Max(g => g.Name.Length);
        foreach (var g in groups)
            w.WriteLine($"  {g.Name.PadRight(width)}  {g.Description}");
        w.WriteLine();
        w.WriteLine("Other commands:");
        w.WriteLine("  help [group [command]]   This help, or help for a group or command");
        w.WriteLine("  schema [group]           Every command and parameter as JSON (for agents and tools)");
        w.WriteLine("  mcp                      Serve every command as an MCP tool over stdio");
        w.WriteLine("  version                  Print the version");
        w.WriteLine();
        w.WriteLine(GlobalOptionsText);
        w.WriteLine();
        WriteExitCodes(w);
        w.WriteLine();
        w.WriteLine("Start with:  lrw profiles add prod --host 203.0.113.10 --key ~/.ssh/id_ed25519");
        w.WriteLine("             lrw connection test");
        w.WriteLine("             lrw help nginx");
    }

    /// <summary>Help for a group, or for a partial path such as "nginx sites".</summary>
    public static bool WriteGroup(CommandCatalog catalog, IReadOnlyList<string> words, TextWriter w)
    {
        var prefix = string.Join(' ', words);
        var commands = catalog.For(Surface.Cli).Where(c => c.FullPath == prefix || c.FullPath.StartsWith(prefix + " ")).ToList();
        if (commands.Count == 0) return false;

        var group = catalog.FindGroup(words[0]);
        w.WriteLine($"lrw {prefix}" + (group != null && words.Count == 1 ? $" - {group.Description}" : ""));
        w.WriteLine();
        var width = commands.Max(c => c.FullPath.Length);
        foreach (var c in commands)
            w.WriteLine($"  {c.FullPath.PadRight(width)}  {Markers(c)}{FirstSentence(c.Description)}");
        w.WriteLine();
        w.WriteLine($"Details: lrw help {prefix} <command>   (or append --help)");
        return true;
    }

    public static void WriteCommand(CommandSpec c, TextWriter w)
    {
        w.WriteLine($"lrw {c.FullPath}");
        w.WriteLine();
        w.WriteLine($"  {c.Description}");
        w.WriteLine();
        w.WriteLine($"Usage: lrw {c.FullPath}{Usage(c)}");

        if (c.Parameters.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Options:");
            var labels = c.Parameters.Select(Label).ToList();
            var width = labels.Max(l => l.Length);
            for (var i = 0; i < c.Parameters.Count; i++)
            {
                var p = c.Parameters[i];
                var notes = new List<string>();
                if (p.Required) notes.Add("required");
                else if (p.DefaultForDisplay is { } d && p.Kind != ParamKind.Bool) notes.Add($"default: {d}");
                if (p.AllowFile) notes.Add("@file or @- for stdin");
                if (p.Secret) notes.Add("also env:VAR or @file");
                var suffix = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
                w.WriteLine($"  {labels[i].PadRight(width)}  {p.Description}{suffix}");
            }
        }

        var facts = new List<string>();
        if (c.Attribute.Local) facts.Add("Runs locally - no SSH connection.");
        if (c.Attribute.ReadOnly) facts.Add("Read-only.");
        if (c.Destructive) facts.Add("Destructive - refused without --yes.");
        if (c.Attribute.Unrestricted) facts.Add("Unrestricted - can change anything; review what you pass.");
        if (c.Attribute.Streams) facts.Add("Streams remote output to stderr and can take minutes.");
        if (facts.Count > 0)
        {
            w.WriteLine();
            foreach (var fact in facts) w.WriteLine($"  {fact}");
        }

        if (c.Attribute.Example is { } example)
        {
            w.WriteLine();
            w.WriteLine($"Example: lrw {example}");
        }
    }

    public static void WriteExitCodes(TextWriter w)
    {
        w.WriteLine("Exit codes:");
        foreach (var (code, meaning) in ExitCodes.Meaning)
            w.WriteLine($"  {code,3}  {meaning}");
    }

    /// <summary>Every command with its parameters, for agents: what `lrw schema` prints.</summary>
    public static JsonObject Schema(CommandCatalog catalog, string? group)
    {
        var commands = new JsonArray();
        foreach (var c in catalog.Commands.Where(c => group == null || c.Group == group))
        {
            var parameters = new JsonArray();
            foreach (var p in c.Parameters)
            {
                var param = new JsonObject
                {
                    ["name"] = p.Name,
                    ["option"] = p.Positional ? $"<{p.CliName}> or --{p.CliName}" : $"--{p.CliName}",
                    ["type"] = p.TypeName,
                    ["required"] = p.Required,
                    ["description"] = p.Description
                };
                if (!p.Required && p.DefaultNode is { } d) param["default"] = d;
                if (p.Choices.Count > 0) param["choices"] = new JsonArray(p.Choices.Select(x => (JsonNode?)x).ToArray());
                if (p.AllowFile) param["acceptsFile"] = true;
                if (p.Secret) param["secret"] = true;
                parameters.Add(param);
            }

            commands.Add(new JsonObject
            {
                ["command"] = c.FullPath,
                ["mcpTool"] = c.On(Surface.Mcp) ? c.ToolName : null,
                ["cli"] = c.On(Surface.Cli),
                ["description"] = c.Description,
                ["readOnly"] = c.Attribute.ReadOnly,
                ["destructive"] = c.Destructive,
                ["unrestricted"] = c.Attribute.Unrestricted,
                ["local"] = c.Attribute.Local,
                ["streams"] = c.Attribute.Streams,
                ["example"] = c.Attribute.Example == null ? null : $"lrw {c.Attribute.Example}",
                ["parameters"] = parameters
            });
        }

        var exitCodes = new JsonObject();
        foreach (var (code, meaning) in ExitCodes.Meaning) exitCodes[code.ToString()] = meaning;

        return new JsonObject
        {
            ["name"] = "lrw",
            ["version"] = Version,
            ["envelope"] = "Every command prints { ok, command, server?, message?, warning?, data?, output? } or " +
                           "{ ok: false, command, error: { kind, message, hint?, detail?, output? } }",
            ["exitCodes"] = exitCodes,
            ["commands"] = commands
        };
    }

    public static string Version =>
        typeof(HelpWriter).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static string Usage(CommandSpec c)
    {
        var parts = new List<string>();
        foreach (var p in c.Parameters.Where(p => p.Positional))
            parts.Add(p.Required ? $" <{p.CliName}>" : $" [{p.CliName}]");
        foreach (var p in c.Parameters.Where(p => !p.Positional))
        {
            var text = p.Kind == ParamKind.Bool ? $"--{p.CliName}" : $"--{p.CliName} {ValueHint(p)}";
            parts.Add(p.Required ? $" {text}" : $" [{text}]");
        }
        return string.Concat(parts);
    }

    private static string Label(ParameterSpec p)
    {
        var name = p.Short != null ? $"{p.Short}, --{p.CliName}" : $"--{p.CliName}";
        if (p.Positional) name = $"<{p.CliName}>, {name}";
        return p.Kind == ParamKind.Bool ? name : $"{name} {ValueHint(p)}";
    }

    private static string ValueHint(ParameterSpec p) =>
        p.Choices.Count > 0 ? string.Join('|', p.Choices) : p.Kind == ParamKind.Int ? "<n>" : "<text>";

    private static string Markers(CommandSpec c) =>
        c.Destructive ? "[destructive] " : c.Attribute.Local ? "[local] " : "";

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text.TrimEnd('.') : text[..end];
    }
}
