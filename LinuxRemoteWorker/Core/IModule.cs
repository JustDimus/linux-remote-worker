namespace LinuxRemoteWorker.Core;

public interface IModule
{
    string Title { get; }
    string Icon { get; }
    Task LoadAsync(SshService ssh);
}
