namespace LinuxRemoteWorker.Modules.Postgres;

public record HbaRule(string Type, string Database, string User, string Address, string Method, string Raw);
