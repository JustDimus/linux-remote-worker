namespace LinuxRemoteWorker.Core;

/// <summary>A directory entry on the server, as listed over SFTP or by <c>find -printf</c>.</summary>
public sealed record RemoteFileInfo(
    string Name,
    string FullName,
    bool IsDirectory,
    long Size,
    DateTime ModifiedUtc);
