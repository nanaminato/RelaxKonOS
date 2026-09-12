using System.Collections.Concurrent;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Installations;

/// <summary>
/// Keeps selected archive paths out of the durable installation protocol. References are
/// short-lived, bound to their creator and revalidated before the worker opens the file.
/// </summary>
public sealed class InstallationFileReferenceStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public InstallationFileReferenceDto Create(InstallationServiceId service, string actor, string path)
    {
        PurgeExpired();
        if (!TryInspect(path, out var fullPath, out var info))
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 400);
        var id = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        entries[id] = new Entry(service, InstallationOperationStore.Reference(actor), fullPath, info.Name, info.Length, expires, null);
        return new(id, info.Name, info.Length, expires);
    }

    /// <summary>Registers a server-private uploaded file. It is removed after use or expiry.</summary>
    public InstallationFileReferenceDto RegisterStaged(InstallationServiceId service, string actor, string path, string fileName, Action cleanup)
    {
        PurgeExpired();
        if (!TryInspect(path, out var fullPath, out var info))
        {
            cleanup();
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 400);
        }
        var id = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        entries[id] = new Entry(service, InstallationOperationStore.Reference(actor), fullPath, Path.GetFileName(fileName), info.Length, expires, cleanup);
        return new(id, Path.GetFileName(fileName), info.Length, expires);
    }

    public InstallationFileSource Open(InstallationServiceId service, string actor, string id)
    {
        PurgeExpired();
        if (string.IsNullOrWhiteSpace(id) || !entries.TryRemove(id, out var entry))
        {
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 409);
        }
        if (entry.Service != service || entry.ActorReference != InstallationOperationStore.Reference(actor)
            || !TryInspect(entry.Path, out var path, out var info) || info.Length != entry.Length)
        {
            entry.Cleanup?.Invoke();
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 409);
        }
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return new(stream, entry.FileName, info.Length, entry.Cleanup);
        }
        catch
        {
            entry.Cleanup?.Invoke();
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 409);
        }
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in entries.Where(pair => pair.Value.ExpiresAt <= now))
            if (entries.TryRemove(pair.Key, out var entry)) entry.Cleanup?.Invoke();
    }

    private static bool TryInspect(string value, out string path, out FileInfo info)
    {
        path = string.Empty; info = null!;
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            if (Directory.Exists(path) || !File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
            info = new FileInfo(path);
            return info.Exists && info.Length >= 0;
        }
        catch { return false; }
    }

    private sealed record Entry(InstallationServiceId Service, string ActorReference, string Path, string FileName, long Length,
        DateTimeOffset ExpiresAt, Action? Cleanup);
}

public sealed class InstallationFileSource(Stream stream, string fileName, long length, Action? cleanup) : IDisposable
{
    public Stream Stream { get; } = stream;
    public string FileName { get; } = fileName;
    public long Length { get; } = length;
    public void Dispose()
    {
        Stream.Dispose();
        cleanup?.Invoke();
    }
}
