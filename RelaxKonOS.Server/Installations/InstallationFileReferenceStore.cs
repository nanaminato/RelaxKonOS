using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Server.Installations;

/// <summary>
/// Keeps selected archive paths out of the durable installation protocol. References are
/// short-lived, bound to their creator and revalidated before the worker opens the file.
/// </summary>
public sealed class InstallationFileReferenceStore(IHostEnvironment environment, ILogger<InstallationFileReferenceStore> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    // Every currently supported managed runtime has a 128 MiB archive limit. Keep the shared
    // transport cap at that boundary so a malformed upload cannot consume host disk first.
    public const long MaximumUploadedPackageBytes = 128L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly string uploadRoot = Path.Combine(environment.ContentRootPath, "data", "installation-packages");

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

    /// <summary>
    /// Copies a package selected or downloaded by the desktop host into a private, short-lived
    /// server staging area. The installation worker consumes it through the same actor-bound
    /// reference used for server-resident files, so client paths never enter an operation record.
    /// </summary>
    public async Task<InstallationFileReferenceDto> StageUploadAsync(InstallationServiceId service, string actor,
        string fileName, Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        PurgeExpired();
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Length > 128 || declaredLength is > MaximumUploadedPackageBytes)
            throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 400);

        Directory.CreateDirectory(uploadRoot);
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(uploadRoot, id + ".package");
        try
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > MaximumUploadedPackageBytes)
                    throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 400);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total == 0 || declaredLength is { } length && length != total)
                throw new InstallationException(InstallationProblemCodes.FileReferenceUnavailable, 400);
            return RegisterStaged(service, actor, destination, safeFileName, () => DeleteStaged(destination));
        }
        catch
        {
            DeleteStaged(destination);
            throw;
        }
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

    private void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException exception) { logger.LogWarning(exception, "Unable to remove expired installation package."); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Unable to remove expired installation package."); }
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
