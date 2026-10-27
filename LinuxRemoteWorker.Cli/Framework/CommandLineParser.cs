using LinuxRemoteWorker.Cli.Session;

namespace LinuxRemoteWorker.Cli.Framework;

public enum OutputFormat { Text, Json }

/// <summary>Options that apply to every command: which server, output format, verbosity.</summary>
public sealed class GlobalOptions
{
    public ConnectionOptions Connection { get; set; } = new();
    public OutputFormat? Format { get; set; }
    public bool Quiet { get; set; }
    public bool Help { get; set; }
}

public sealed class ParsedCommandLine
{
    public required GlobalOptions Globals { get; init; }

    /// <summary>help, version, schema or mcp — handled by the app itself, not the catalog.</summary>
    public string? Builtin { get; init; }

    /// <summary>The words that were not consumed as a command path or its positionals.</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    public CommandSpec? Command { get; init; }
    public Dictionary<string, object?> Values { get; init; } = new();

    /// <summary>The command line with secret values replaced by ***, for the app log.</summary>
    public IReadOnlyList<string> Redacted { get; init; } = [];
}

/// <summary>
/// lrw [globals] &lt;group&gt; &lt;command words&gt; [positionals] [--option value | --flag | --no-flag | -y] [-- rest]
/// Global options may appear anywhere; an option the command itself defines wins over a global one
/// (so `profiles add --host` sets the profile's host, not the connection's).
/// </summary>
public static class CommandLineParser
{
    public static readonly string[] Builtins = ["help", "version", "schema", "mcp"];

    private static readonly Dictionary<string, Action<GlobalOptions, string>> ValueGlobals = new()
    {
        ["--profile"] = (g, v) => g.Connection = g.Connection with { Profile = v },
        ["--host"] = (g, v) => g.Connection = g.Connection with { Host = v },
        ["--user"] = (g, v) => g.Connection = g.Connection with { User = v },
        ["--key"] = (g, v) => g.Connection = g.Connection with { Key = v },
        ["--passphrase-env"] = (g, v) => g.Connection = g.Connection with { PassphraseEnv = v },
        ["--passphrase-file"] = (g, v) => g.Connection = g.Connection with { PassphraseFile = v },
        ["--password-env"] = (g, v) => g.Connection = g.Connection with { PasswordEnv = v },
        ["--password-file"] = (g, v) => g.Connection = g.Connection with { PasswordFile = v },
        ["--output"] = (g, v) => g.Format = ParseFormat(v),
        ["-o"] = (g, v) => g.Format = ParseFormat(v)
    };

    private static readonly Dictionary<string, Action<GlobalOptions>> FlagGlobals = new()
    {
        ["--json"] = g => g.Format = OutputFormat.Json,
        ["--text"] = g => g.Format = OutputFormat.Text,
        ["--quiet"] = g => g.Quiet = true,
        ["-q"] = g => g.Quiet = true,
        ["--help"] = g => g.Help = true,
        ["-h"] = g => g.Help = true
    };

    /// <summary>Reads just the output format, so even a command line that fails to parse can answer in JSON.</summary>
    public static OutputFormat? PeekFormat(string[] args)
    {
        OutputFormat? format = null;
        for (var i = 0; i < args.Length; i++)
        {
            var (name, inline) = Split(args[i]);
            if (name == "--json") format = OutputFormat.Json;
            else if (name == "--text") format = OutputFormat.Text;
            else if (name is "--output" or "-o" && (inline ?? (i + 1 < args.Length ? args[i + 1] : null)) is { } v)
                format = v.Equals("json", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Json : OutputFormat.Text;
        }
        return format;
    }

    public static ParsedCommandLine Parse(CommandCatalog catalog, string[] args)
    {
        var globals = new GlobalOptions();
        var redacted = new List<string>();
        var words = new List<string>();
        var i = 0;

        // Phase 1: leading words (command path, positionals) and global options, up to the first command option
        while (i < args.Length)
        {
            var token = args[i];
            if (token == "--") break;
            if (IsOption(token))
            {
                var (command, _) = Resolve(catalog, words);
                if (command != null && FindParameter(command, Split(token).Name, out _) != null) break;
                if (!TryGlobal(args, ref i, globals, redacted)) break;
                continue;
            }
            words.Add(token);
            redacted.Add(token);
            i++;
        }

        if (words.Count > 0 && Builtins.Contains(words[0]))
        {
            ParseGlobalsOnly(args, ref i, globals, redacted, words, allowWords: true);
            return new ParsedCommandLine { Globals = globals, Builtin = words[0], Words = words.Skip(1).ToList(), Redacted = redacted };
        }

        var (spec, consumed) = Resolve(catalog, words);
        if (spec == null)
        {
            ParseGlobalsOnly(args, ref i, globals, redacted, words, allowWords: false);
            return new ParsedCommandLine { Globals = globals, Words = words, Redacted = redacted };
        }

        // Phase 2: the command's own options and positionals (globals still allowed)
        var values = new Dictionary<string, object?>();
        var positionals = words.Skip(consumed).ToList();
        while (i < args.Length)
        {
            var token = args[i];
            if (token == "--")
            {
                positionals.AddRange(args.Skip(i + 1));
                redacted.AddRange(args.Skip(i));
                break;
            }

            if (!IsOption(token))
            {
                positionals.Add(token);
                redacted.Add(token);
                i++;
                continue;
            }

            var (name, inline) = Split(token);
            var parameter = FindParameter(spec, name, out var negated);
            if (parameter == null)
            {
                if (TryGlobal(args, ref i, globals, redacted)) continue;
                throw new UsageException($"Unknown option {name} for 'lrw {spec.FullPath}'.{Suggest(spec, name)}", spec.FullPath);
            }

            i++;
            if (parameter.Kind == ParamKind.Bool && (inline == null || negated))
            {
                values[parameter.Name] = !negated;
                redacted.Add(token);
                continue;
            }

            var raw = inline ?? (i < args.Length ? args[i++] : throw new UsageException($"{name} needs a value.", spec.FullPath));
            values[parameter.Name] = ValueConverter.FromText(parameter, raw, indirect: true);
            redacted.Add(parameter.Secret ? $"{name} ***" : inline != null ? token : $"{name} {raw}");
        }

        AssignPositionals(spec, positionals, values);

        if (!globals.Help)
        {
            var missing = spec.Parameters.Where(p => p.Required && !values.ContainsKey(p.Name))
                .Select(p => p.Positional ? $"<{p.CliName}>" : $"--{p.CliName}")
                .ToList();
            if (missing.Count > 0)
                throw new UsageException($"'lrw {spec.FullPath}' needs {string.Join(", ", missing)}.", spec.FullPath);
        }

        return new ParsedCommandLine { Globals = globals, Command = spec, Values = values, Redacted = redacted };
    }

    /// <summary>The longest run of leading words that names a CLI command, and how many words it used.</summary>
    public static (CommandSpec? Command, int Consumed) Resolve(CommandCatalog catalog, IReadOnlyList<string> words)
    {
        for (var n = words.Count; n >= 1; n--)
        {
            var path = string.Join(' ', words.Take(n));
            var command = catalog.For(Surface.Cli).FirstOrDefault(c => c.FullPath == path);
            if (command != null) return (command, n);
        }
        return (null, 0);
    }

    private static void AssignPositionals(CommandSpec spec, List<string> positionals, Dictionary<string, object?> values)
    {
        var slots = spec.Parameters.Where(p => p.Positional && !values.ContainsKey(p.Name)).ToList();
        var next = 0;
        foreach (var slot in slots)
        {
            if (next >= positionals.Count) break;
            var raw = slot.Rest ? string.Join(' ', positionals.Skip(next)) : positionals[next];
            next = slot.Rest ? positionals.Count : next + 1;
            values[slot.Name] = ValueConverter.FromText(slot, raw, indirect: true);
        }

        if (next < positionals.Count)
            throw new UsageException($"Unexpected argument '{positionals[next]}' for 'lrw {spec.FullPath}'.", spec.FullPath);
    }

    private static void ParseGlobalsOnly(string[] args, ref int i, GlobalOptions globals, List<string> redacted,
        List<string> words, bool allowWords)
    {
        while (i < args.Length)
        {
            if (args[i] == "--") { i++; continue; }
            if (IsOption(args[i]))
            {
                if (!TryGlobal(args, ref i, globals, redacted))
                    throw new UsageException($"Unknown option {Split(args[i]).Name}.");
                continue;
            }
            if (!allowWords && !globals.Help)
                throw new UsageException($"Unknown command 'lrw {string.Join(' ', words.Append(args[i]))}'.");
            words.Add(args[i]);
            redacted.Add(args[i]);
            i++;
        }
    }

    private static bool TryGlobal(string[] args, ref int i, GlobalOptions globals, List<string> redacted)
    {
        var (name, inline) = Split(args[i]);
        if (FlagGlobals.TryGetValue(name, out var flag))
        {
            flag(globals);
            redacted.Add(args[i]);
            i++;
            return true;
        }

        if (!ValueGlobals.TryGetValue(name, out var set)) return false;
        var value = inline ?? (i + 1 < args.Length ? args[i + 1] : throw new UsageException($"{name} needs a value."));
        set(globals, value);
        redacted.Add(inline != null ? args[i] : $"{name} {value}");
        i += inline != null ? 1 : 2;
        return true;
    }

    /// <summary>Finds a command parameter by --kebab-name, --camelName, -short, --no-flag, or --confirm for --yes.</summary>
    private static ParameterSpec? FindParameter(CommandSpec spec, string name, out bool negated)
    {
        negated = false;
        foreach (var p in spec.Parameters)
        {
            if (name == $"--{p.CliName}" || name.Equals($"--{p.Name}", StringComparison.OrdinalIgnoreCase)
                || (p.Short != null && name == p.Short) || (p.IsConfirm && name == "--confirm"))
                return p;
        }

        foreach (var p in spec.Parameters.Where(p => p.Kind == ParamKind.Bool))
        {
            if (name == $"--no-{p.CliName}")
            {
                negated = true;
                return p;
            }
        }
        return null;
    }

    private static string Suggest(CommandSpec spec, string name)
    {
        var bare = name.TrimStart('-');
        var close = spec.Parameters
            .Where(p => p.CliName.Contains(bare) || bare.Contains(p.CliName) || Distance(p.CliName, bare) <= 2)
            .Select(p => $"--{p.CliName}")
            .ToList();
        return close.Count > 0 ? $" Did you mean {string.Join(" or ", close)}?" : "";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var x = 0; x <= a.Length; x++) d[x, 0] = x;
        for (var y = 0; y <= b.Length; y++) d[0, y] = y;
        for (var x = 1; x <= a.Length; x++)
        for (var y = 1; y <= b.Length; y++)
            d[x, y] = Math.Min(Math.Min(d[x - 1, y] + 1, d[x, y - 1] + 1), d[x - 1, y - 1] + (a[x - 1] == b[y - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // "-x", "--name" and "--name=value" are options; "-" alone and anything else are words
    private static bool IsOption(string token) => token.Length > 1 && token[0] == '-';

    private static (string Name, string? Inline) Split(string token)
    {
        var eq = token.IndexOf('=');
        return token.StartsWith("--") && eq > 2 ? (token[..eq], token[(eq + 1)..]) : (token, null);
    }

    private static OutputFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "json" => OutputFormat.Json,
        "text" => OutputFormat.Text,
        _ => throw new UsageException($"--output expects json or text, got '{value}'.")
    };
}
