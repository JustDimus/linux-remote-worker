namespace LinuxRemoteWorker.Modules.Postgres;

/// <summary>One rule of pg_hba.conf. <see cref="File"/> + <see cref="LineNumber"/> identify it for removal.</summary>
public record HbaRule(int LineNumber, string Type, string Database, string User, string Address, string Method)
{
    /// <summary>The file the rule is written in (pg_hba.conf, or an included file on PostgreSQL 16+).</summary>
    public string File { get; init; } = string.Empty;
}
