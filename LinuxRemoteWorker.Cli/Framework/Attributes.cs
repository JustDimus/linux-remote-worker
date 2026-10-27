namespace LinuxRemoteWorker.Cli.Framework;

/// <summary>Where a command is offered.</summary>
[Flags]
public enum Surface
{
    Cli = 1,
    Mcp = 2,
    All = Cli | Mcp
}

/// <summary>A class of commands sharing the first word of their path, e.g. "nginx".</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandGroupAttribute : Attribute
{
    public CommandGroupAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
}

/// <summary>
/// Turns a public method of a <see cref="CommandGroupAttribute"/> class into a command. Its parameters
/// become options (CLI) and input properties (MCP); its return value becomes the result's data.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string path, string description)
    {
        Path = path;
        Description = description;
    }

    /// <summary>The words after the group name, e.g. "sites create".</summary>
    public string Path { get; }

    public string Description { get; }

    /// <summary>Changes nothing anywhere.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Deletes data or takes something offline: refused unless confirmed (--yes / confirm=true).</summary>
    public bool Destructive { get; set; }

    /// <summary>Not destructive by itself, but can do anything (raw shell, SQL, config text) - flagged as such to MCP clients.</summary>
    public bool Unrestricted { get; set; }

    /// <summary>Repeating it with the same input changes nothing more.</summary>
    public bool Idempotent { get; set; }

    /// <summary>Runs on this computer only and never opens an SSH connection.</summary>
    public bool Local { get; set; }

    /// <summary>Streams remote output while it runs (installs, builds, certbot) and may take minutes.</summary>
    public bool Streams { get; set; }

    public Surface Surface { get; set; } = Surface.All;

    /// <summary>One example invocation, shown in help (without the leading "lrw ").</summary>
    public string? Example { get; set; }
}

/// <summary>Describes a command parameter.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ArgAttribute : Attribute
{
    public ArgAttribute(string description)
    {
        Description = description;
    }

    public string Description { get; }

    /// <summary>May be given without its --name, in declaration order (CLI only).</summary>
    public bool Positional { get; set; }

    /// <summary>The last positional parameter collects every remaining word, joined by spaces (CLI only).</summary>
    public bool Rest { get; set; }

    /// <summary>A one-letter alias such as "-n".</summary>
    public string? Short { get; set; }

    /// <summary>Never written to the app log. The CLI also accepts env:NAME and @file for it.</summary>
    public bool Secret { get; set; }

    /// <summary>The CLI reads "@path" from a file and "@-" from stdin (config text, SQL, unit files).</summary>
    public bool AllowFile { get; set; }

    /// <summary>The only accepted values of a string parameter.</summary>
    public string[]? Choices { get; set; }

    /// <summary>
    /// A path on the server. On the CLI a Windows-style value (C:/...) is refused with an explanation: Git Bash
    /// rewrites arguments that start with / into Windows paths before the program sees them.
    /// </summary>
    public bool RemotePath { get; set; }
}
