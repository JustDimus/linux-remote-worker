using System.Globalization;
using LinuxRemoteWorker.Cli.Framework;

namespace LinuxRemoteWorker.Cli.Interactive;

internal enum FieldKind
{
    Text,
    Number,
    Secret,

    /// <summary>Several lines: config files, SQL, unit files, public keys.</summary>
    Content,
    Toggle,
    Choice
}

/// <summary>One parameter of a command as an input field: what is typed in it, and what it means for the command.</summary>
internal sealed class FormField
{
    public FormField(ParameterSpec parameter)
    {
        Parameter = parameter;
        Kind = parameter.Kind switch
        {
            ParamKind.Bool => FieldKind.Toggle,
            ParamKind.Enum => FieldKind.Choice,
            ParamKind.Int => FieldKind.Number,
            _ when parameter.Choices.Count > 0 => FieldKind.Choice,
            _ when parameter.Secret => FieldKind.Secret,
            _ when parameter.AllowFile => FieldKind.Content,
            _ => FieldKind.Text
        };

        Default = parameter.DefaultForDisplay switch
        {
            null => Kind == FieldKind.Toggle ? "false" : "",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString() ?? ""
        };

        if (Kind == FieldKind.Choice)
        {
            // An option the user can leave out (no default) gets an empty first choice: "let the command decide"
            Options = parameter.Choices.Contains(Default, StringComparer.OrdinalIgnoreCase)
                ? parameter.Choices
                : ["", .. parameter.Choices];
            Default = Options.FirstOrDefault(o => o.Equals(Default, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        Value = Default;
    }

    public ParameterSpec Parameter { get; }
    public FieldKind Kind { get; }

    /// <summary>The CLI option name, which is also the field's label.</summary>
    public string Name => Parameter.CliName;

    /// <summary>The default as it would be typed; empty when the parameter has none.</summary>
    public string Default { get; }

    /// <summary>A choice field's values; an empty first one means "leave it out".</summary>
    public IReadOnlyList<string> Options { get; } = [];

    /// <summary>
    /// What the field holds: the text typed (text, number, secret, content), "true"/"false" (toggle), or one of
    /// <see cref="Options"/> (choice). It starts at the default.
    /// </summary>
    public string Value { get; set; }

    /// <summary>Why the value was refused the last time the form was submitted; null when it was fine.</summary>
    public string? Error { get; set; }

    public bool Required => Parameter.Required && Kind != FieldKind.Toggle;

    public bool Toggled
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }

    /// <summary>The value to pass to the command; null when the field is left at its default (or empty).</summary>
    public string? Given
    {
        get
        {
            switch (Kind)
            {
                case FieldKind.Secret:
                    return Value.Length == 0 ? null : Value;
                case FieldKind.Content:
                    return string.IsNullOrWhiteSpace(Value) || Value == Default ? null : Value;
                case FieldKind.Toggle:
                    // A required switch has no default to fall back to, so its value always goes along
                    return Value == Default && !Parameter.Required ? null : Value;
                default:
                    var text = Value.Trim();
                    return text.Length == 0 || text == Default ? null : text;
            }
        }
    }

    /// <summary>What the field is for, with the facts a user needs: required, default, accepted forms.</summary>
    public string Help
    {
        get
        {
            var notes = new List<string>();
            if (Required) notes.Add("required");
            else if (Default.Length > 0 && Kind is not (FieldKind.Toggle or FieldKind.Choice)) notes.Add($"default: {Default}");
            if (Parameter.AllowFile) notes.Add("@path reads a file");
            if (Parameter.Secret) notes.Add("also env:NAME or @file; shown as ***");
            var text = Parameter.Description.TrimEnd('.');
            return notes.Count == 0 ? text : $"{text} ({string.Join("; ", notes)})";
        }
    }
}

/// <summary>
/// The input form of one command, built from its parameters. It turns what was typed into the values the command
/// takes, exactly as the command line parser would, and shows the command line that does the same thing.
/// </summary>
internal sealed class CommandForm
{
    public CommandForm(CommandSpec command)
    {
        Command = command;
        // The confirm switch is not a field: a destructive command asks before it runs
        Fields = command.Parameters.Where(p => !p.IsConfirm).Select(p => new FormField(p)).ToList();
    }

    public CommandSpec Command { get; }
    public IReadOnlyList<FormField> Fields { get; }

    public FormField? FirstError => Fields.FirstOrDefault(f => f.Error != null);

    /// <summary>Fills in a field by its option name (what a screen knows: the selected service, site ...).</summary>
    public void Set(string name, string value)
    {
        if (Fields.FirstOrDefault(f => f.Name == name) is { } field) field.Value = value;
    }

    /// <summary>
    /// Converts every field; false when one is missing or wrong, with its <see cref="FormField.Error"/> saying why.
    /// </summary>
    public bool TryBuild(out Dictionary<string, object?> values)
    {
        values = new Dictionary<string, object?>();
        var ok = true;
        foreach (var field in Fields)
        {
            field.Error = null;
            var given = field.Given;
            if (given == null)
            {
                if (field.Required)
                {
                    field.Error = $"{field.Name} is required.";
                    ok = false;
                }
                continue;
            }

            if (given.Trim() == "@-")
            {
                field.Error = $"{field.Name}: standard input (@-) cannot be read here - type the text, or @path to read a file.";
                ok = false;
                continue;
            }

            try
            {
                values[field.Parameter.Name] = ValueConverter.FromText(field.Parameter, given, indirect: true);
            }
            catch (UsageException ex)
            {
                field.Error = ex.Message;
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// The same command typed on the command line, with secrets as ***. It teaches the commands while the menus are
    /// used, and goes to the app log.
    /// </summary>
    /// <param name="profile">The profile chosen in this session, when it is not simply the default.</param>
    /// <param name="windows">Quote for PowerShell instead of a POSIX shell.</param>
    public string CommandLine(string? profile, bool windows)
    {
        var parts = new List<string> { "lrw", Command.FullPath };
        var bare = true; // positionals can go without their --name while every earlier one was given
        foreach (var field in Fields)
        {
            var p = field.Parameter;
            var given = field.Given;
            if (p.Positional)
            {
                if (given == null)
                {
                    bare = false;
                    continue;
                }
                if (bare && !given.StartsWith('-') && field.Kind != FieldKind.Toggle)
                {
                    parts.Add(Shown(field, given, windows));
                    continue;
                }
            }

            if (given == null) continue;
            if (field.Kind == FieldKind.Toggle)
            {
                parts.Add(field.Toggled ? $"--{p.CliName}" : $"--no-{p.CliName}");
                continue;
            }
            parts.Add($"--{p.CliName}");
            parts.Add(Shown(field, given, windows));
        }

        if (Command.Destructive) parts.Add("--yes");
        if (profile != null)
        {
            parts.Add("--profile");
            parts.Add(Quote(profile, windows));
        }
        return string.Join(' ', parts);
    }

    private static string Shown(FormField field, string given, bool windows) => field.Kind switch
    {
        FieldKind.Secret => "***",
        FieldKind.Content when given.Contains('\n') => $"<{given.TrimEnd('\n').Split('\n').Length} lines>",
        _ => Quote(given, windows)
    };

    /// <summary>A word as a shell takes it: as it is when that is safe, else in single quotes.</summary>
    public static string Quote(string value, bool windows)
    {
        // PowerShell reads a leading @ as splatting and a comma as an array, so those are quoted there
        var safe = value.Length > 0 && value[0] != '~'
                   && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=+%".Contains(c) || (!windows && c is '@' or ','));
        if (safe) return value;
        return windows ? $"'{value.Replace("'", "''")}'" : $"'{value.Replace("'", "'\\''")}'";
    }
}
