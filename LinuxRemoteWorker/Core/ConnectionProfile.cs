namespace LinuxRemoteWorker.Core;

public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = "root";
    public string PrivateKeyPath { get; set; } = string.Empty;
}
