namespace LinuxRemoteWorker.Modules.Postgres;

public record PgUser(string Name, bool IsSuperuser, bool CanCreateDb);
