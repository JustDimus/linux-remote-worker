using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace LinuxRemoteWorker.Cli.Framework;

public enum ParamKind { String, Int, Bool, Enum }

/// <summary>One input of a command, as the CLI parser, help, schema and MCP see it.</summary>
public sealed class ParameterSpec
{
    /// <summary>The C# parameter name (camelCase) — the MCP property and JSON name.</summary>
    public required string Name { get; init; }

    /// <summary>The CLI option name without dashes (kebab-case).</summary>
    public required string CliName { get; init; }

    public required string Description { get; init; }
    public required ParamKind Kind { get; init; }

    /// <summary>The CLR type with Nullable&lt;&gt; removed.</summary>
    public required Type Type { get; init; }

    public bool Required { get; init; }
    public object? Default { get; init; }
    public bool Positional { get; init; }
    public bool Rest { get; init; }
    public string? Short { get; init; }
    public bool Secret { get; init; }
    public bool AllowFile { get; init; }
    public bool RemotePath { get; init; }

    /// <summary>Accepted values (kebab-case enum members, or a string parameter's choices).</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The confirm switch every destructive command gets; it is checked, never passed to the method.</summary>
    public bool IsConfirm { get; init; }

    public string TypeName => Kind switch
    {
        ParamKind.Int => "integer",
        ParamKind.Bool => "boolean",
        _ => "string"
    };

    /// <summary>The default as the user would type it (enum defaults in kebab-case).</summary>
    public object? DefaultForDisplay => Default is Enum e ? CommandCatalog.Kebab(e.ToString()) : Default;

    /// <summary>The default as a JSON value, for schemas; null when there is none.</summary>
    public JsonNode? DefaultNode => DefaultForDisplay switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        var other => JsonValue.Create(other.ToString())
    };
}

public sealed class CommandSpec
{
    public required string Group { get; init; }
    public required string Path { get; init; }
    public required string Description { get; init; }
    public required CommandAttribute Attribute { get; init; }
    public required MethodInfo Method { get; init; }
    public required IReadOnlyList<ParameterSpec> Parameters { get; init; }

    /// <summary>"nginx sites create"; just the group name for a command with an empty path ("update").</summary>
    public string FullPath => Path.Length == 0 ? Group : $"{Group} {Path}";

    /// <summary>MCP tool name: "nginx sites create" → "nginx_sites_create".</summary>
    public string ToolName => FullPath.Replace(' ', '_').Replace('-', '_');

    public bool Destructive => Attribute.Destructive;
    public bool On(Surface surface) => (Attribute.Surface & surface) != 0;
}

public sealed record GroupSpec(string Name, string Description, IReadOnlyList<CommandSpec> Commands);

/// <summary>
/// Every command of the assembly, found by reflection over [CommandGroup] classes and their [Command]
/// methods. The one source the CLI, help, `lrw schema` and the MCP server are generated from.
/// </summary>
public sealed class CommandCatalog
{
    public const string ConfirmName = "confirm";

    private CommandCatalog(IReadOnlyList<GroupSpec> groups)
    {
        Groups = groups;
        Commands = groups.SelectMany(g => g.Commands).ToList();
    }

    public IReadOnlyList<GroupSpec> Groups { get; }
    public IReadOnlyList<CommandSpec> Commands { get; }

    public IEnumerable<CommandSpec> For(Surface surface) => Commands.Where(c => c.On(surface));

    public GroupSpec? FindGroup(string name) => Groups.FirstOrDefault(g => g.Name == name);

    public static CommandCatalog Build(Assembly assembly)
    {
        var groups = new List<GroupSpec>();
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<CommandGroupAttribute>() != null))
        {
            var group = type.GetCustomAttribute<CommandGroupAttribute>()!;
            var commands = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => (Method: m, Attr: m.GetCustomAttribute<CommandAttribute>()))
                .Where(x => x.Attr != null)
                .Select(x => BuildCommand(group.Name, x.Method, x.Attr!))
                .ToList();
            groups.Add(new GroupSpec(group.Name, group.Description, commands));
        }

        var duplicate = groups.SelectMany(g => g.Commands).GroupBy(c => c.FullPath).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException($"Command '{duplicate.Key}' is defined twice.");

        return new CommandCatalog(groups.OrderBy(g => GroupOrder(g.Name)).ThenBy(g => g.Name).ToList());
    }

    // The order a person reads help in: connect first, then the modules, local diagnostics last
    private static readonly string[] HelpOrder =
        ["profiles", "connection", "system", "files", "pg", "repos", "dotnet", "services", "logs", "nginx", "certs", "firewall", "security", "applog"];

    private static int GroupOrder(string name)
    {
        var i = Array.IndexOf(HelpOrder, name);
        return i >= 0 ? i : int.MaxValue;
    }

    private static CommandSpec BuildCommand(string group, MethodInfo method, CommandAttribute attr)
    {
        var parameters = method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => BuildParameter(method, p))
            .ToList();

        if (attr.Destructive)
        {
            parameters.Add(new ParameterSpec
            {
                Name = ConfirmName,
                CliName = "yes",
                Short = "-y",
                Kind = ParamKind.Bool,
                Type = typeof(bool),
                Required = false,
                Default = false,
                IsConfirm = true,
                Description = "Confirm this destructive operation. Only set it after the user agreed to exactly this change."
            });
        }

        return new CommandSpec
        {
            Group = group,
            Path = attr.Path,
            Description = attr.Description,
            Attribute = attr,
            Method = method,
            Parameters = parameters
        };
    }

    private static ParameterSpec BuildParameter(MethodInfo method, ParameterInfo p)
    {
        var arg = p.GetCustomAttribute<ArgAttribute>()
                  ?? throw new InvalidOperationException($"{method.DeclaringType!.Name}.{method.Name}: parameter '{p.Name}' has no [Arg].");
        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        var kind = type == typeof(string) ? ParamKind.String
            : type == typeof(int) ? ParamKind.Int
            : type == typeof(bool) ? ParamKind.Bool
            : type.IsEnum ? ParamKind.Enum
            : throw new InvalidOperationException($"{method.Name}: parameter type {type.Name} is not supported.");

        var choices = kind == ParamKind.Enum
            ? Enum.GetNames(type).Select(Kebab).ToList()
            : arg.Choices?.ToList() ?? [];

        return new ParameterSpec
        {
            Name = p.Name!,
            CliName = Kebab(p.Name!),
            Description = arg.Description,
            Kind = kind,
            Type = type,
            Required = !p.HasDefaultValue,
            Default = p.HasDefaultValue ? NormalizeDefault(p.DefaultValue, type) : null,
            Positional = arg.Positional || arg.Rest,
            Rest = arg.Rest,
            Short = arg.Short,
            Secret = arg.Secret,
            AllowFile = arg.AllowFile,
            RemotePath = arg.RemotePath,
            Choices = choices
        };
    }

    // Reflection may report an enum default as its underlying integer, and "no default" as DBNull/Missing
    private static object? NormalizeDefault(object? value, Type type) => value switch
    {
        null or DBNull => null,
        _ when value == Type.Missing => null,
        _ when type.IsEnum && value is not Enum => Enum.ToObject(type, value),
        _ => value
    };

    /// <summary>"maxBody" → "max-body", "ReadWrite" → "read-write", "ServerIpv4" → "server-ipv4".</summary>
    public static string Kebab(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1])) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Parses a kebab-case or PascalCase enum value; null when it matches no member.</summary>
    public static object? ParseEnum(Type type, string text)
    {
        var compact = text.Replace("-", "").Replace("_", "");
        return Enum.GetNames(type).FirstOrDefault(n => n.Equals(compact, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse(type, name)
            : null;
    }
}
