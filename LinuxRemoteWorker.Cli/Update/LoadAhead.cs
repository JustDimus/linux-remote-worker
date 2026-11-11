using System.Reflection;

namespace LinuxRemoteWorker.Cli.Update;

/// <summary>
/// Loads every assembly the program refers to before an update replaces the program under the running process.
/// </summary>
/// <remarks>
/// lrw is published as a single file, and .NET reads an assembly out of that file by its path the first time it is
/// needed. The install script puts the new release at that path (on Windows after renaming the running copy away), so
/// whatever the old process still had to load afterwards - for an error message, the SSH types it checks exceptions
/// against - came from a different file and failed: lrw update crashed after a successful update. An assembly loaded
/// before stays in memory, whatever happens to the file.
/// </remarks>
public static class LoadAhead
{
    /// <summary>Loads the assemblies the loaded ones refer to, and theirs in turn; returns how many there are now.</summary>
    public static int Everything()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (loaded.GetName().Name is { } name && known.Add(name)) pending.Enqueue(loaded);

        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name == null || !known.Add(reference.Name)) continue;
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Referenced but not shipped (a part of another platform): nothing could load it later either
                }
            }
        }
        return known.Count;
    }
}
