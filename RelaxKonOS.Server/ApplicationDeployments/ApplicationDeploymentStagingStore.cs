using System.Collections.Concurrent;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Server-private staging area for deployment archives. A staged archive is bound to the operator
/// who staged it and expires; the host path never appears in a deployment request or an operation
/// record. Entries live under the deployment root, so cleanup can never reach another application's
/// or another user's resources.
/// </summary>
internal sealed class ApplicationDeploymentStagingStore
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly string root;
    private readonly ApplicationDeploymentOptions options;

    public ApplicationDeploymentStagingStore(IHostEnvironment environment, ApplicationDeploymentOptions options)
    {
        this.options = options;
        root = Path.Combine(environment.ContentRootPath, options.RootDirectory, "staging");
    }

    /// <summary>Streams an upload into the staging area, enforcing the byte limit while writing.</summary>
    public async Task<DeploymentStagedFileDto> StageAsync(string fileName, Stream content, string actor, CancellationToken cancellationToken)
    {
        PurgeExpired();
        var safeName = SanitizeFileName(fileName);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, id + ".uploading");
        var destination = Path.Combine(root, id + ".archive");
        long length = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > options.MaximumArchiveBytes)
                        throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveTooLarge, 400);
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
            }
            if (length == 0) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveUnavailable, 400);
            File.Move(temporary, destination);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }

        var expiresAt = Expiry();
        entries[id] = new Entry(ApplicationDeploymentValidation.Reference(actor), safeName, destination, length, expiresAt);
        return new(id, safeName, length, expiresAt);
    }

    /// <summary>
    /// Registers an archive that already exists on the server, for example one chosen in
    /// RemoteExplorer. The reference, not the path, is what a deployment request carries.
    /// </summary>
    public DeploymentStagedFileDto Register(string path, string actor)
    {
        PurgeExpired();
        if (!TryInspect(path, out var fullPath, out var info) || info.Length > options.MaximumArchiveBytes)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.FileReferenceUnavailable, 400);
        var id = Guid.NewGuid().ToString("N");
        var expiresAt = Expiry();
        entries[id] = new Entry(ApplicationDeploymentValidation.Reference(actor), info.Name, fullPath, info.Length, expiresAt);
        return new(id, info.Name, info.Length, expiresAt);
    }

    /// <summary>Opens a staged archive for the operator that staged it; the entry survives to expiry
    /// so a retried deployment can reuse the same upload.</summary>
    public StagedArchive Open(string referenceId, string actor)
    {
        PurgeExpired();
        if (string.IsNullOrWhiteSpace(referenceId) || !entries.TryGetValue(referenceId, out var entry))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.FileReferenceUnavailable, 409);
        if (entry.ActorReference != ApplicationDeploymentValidation.Reference(actor) || entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !TryInspect(entry.Path, out var path, out var info) || info.Length != entry.Length)
        {
            if (entries.TryRemove(referenceId, out var removed)) RemoveFile(removed.Path);
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.FileReferenceUnavailable, 409);
        }
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return new(stream, entry.FileName, entry.Length);
        }
        catch (IOException)
        {
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.FileReferenceUnavailable, 409);
        }
    }

    /// <summary>Drops one staged archive. It only ever removes this store's own staging file.</summary>
    public void Discard(string referenceId, string actor)
    {
        if (string.IsNullOrWhiteSpace(referenceId) || !entries.TryGetValue(referenceId, out var entry)) return;
        if (entry.ActorReference != ApplicationDeploymentValidation.Reference(actor)) return;
        if (entries.TryRemove(referenceId, out var removed)) RemoveFile(removed.Path);
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in entries.Where(pair => pair.Value.ExpiresAt <= now))
            if (entries.TryRemove(pair.Key, out var entry)) RemoveFile(entry.Path);
    }

    private DateTimeOffset Expiry() => DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.StagingLifetimeMinutes, 5, 240));

    private static void RemoveFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* A later purge retries; a failed cleanup never fails the operation. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return "archive.zip";
        var cleaned = new string([.. name.Where(character => !char.IsControl(character))]);
        return cleaned.Length <= 128 ? cleaned : cleaned[..128];
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
            return info.Exists && info.Length > 0;
        }
        catch { return false; }
    }

    private sealed record Entry(string ActorReference, string FileName, string Path, long Length, DateTimeOffset ExpiresAt);
}

/// <summary>A staged archive stream. Its text is never surfaced to a client.</summary>
internal sealed class StagedArchive(Stream stream, string fileName, long length) : IDisposable
{
    public Stream Stream { get; } = stream;
    public string FileName { get; } = fileName;
    public long Length { get; } = length;
    public void Dispose() => Stream.Dispose();
}
