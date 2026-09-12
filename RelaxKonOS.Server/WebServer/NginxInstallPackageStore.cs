using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace RelaxKonOS.Server.WebServer;

/// <summary>Stores only validated, short-lived Windows Nginx ZIP archives outside user-controlled paths.</summary>
internal sealed class NginxInstallPackageStore(IHostEnvironment environment, ILogger<NginxInstallPackageStore> logger)
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "webserver-packages");

    public async Task<string?> SaveAsync(string fileName, Stream content, Func<long, Task>? copied = null, CancellationToken cancellationToken = default)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Rejected Nginx package upload because the host is not Windows. FileName={FileName}", safeFileName);
            return null;
        }
        if (!safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Rejected Nginx package upload because it is not a ZIP archive. FileName={FileName}", safeFileName);
            return null;
        }

        logger.LogInformation("Saving Windows Nginx package upload. FileName={FileName}", safeFileName);
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(_root, $"{id}.uploading");
        var destination = Path.Combine(_root, $"{id}.zip");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumPackageBytes)
                    {
                        logger.LogWarning("Rejected Nginx package upload because it exceeds the size limit. FileName={FileName}, Bytes={Bytes}, MaximumBytes={MaximumBytes}", safeFileName, total, MaximumPackageBytes);
                        return null;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (copied is not null) await copied(total);
                }
            }
            if (!ContainsNginxExecutable(temporary))
            {
                logger.LogWarning("Rejected Nginx package upload because it does not contain nginx.exe. FileName={FileName}", safeFileName);
                return null;
            }
            File.Move(temporary, destination);
            logger.LogInformation("Saved validated Windows Nginx package. PackageId={PackageId}, FileName={FileName}", id, safeFileName);
            return id;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Rejected invalid Nginx ZIP package. FileName={FileName}", safeFileName);
            return null;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to save Nginx package upload. FileName={FileName}", safeFileName);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while saving Nginx package upload. FileName={FileName}", safeFileName);
            return null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string? GetPath(string packageId)
    {
        if (!Guid.TryParseExact(packageId, "N", out _))
        {
            logger.LogWarning("Rejected invalid Nginx package identifier.");
            return null;
        }
        var path = Path.Combine(_root, $"{packageId}.zip");
        if (!File.Exists(path) || IsSymbolicLink(path))
        {
            logger.LogWarning("Nginx package was not available for installation. PackageId={PackageId}", packageId);
            return null;
        }
        return path;
    }

    public void Delete(string? packageId)
    {
        var path = packageId is null ? null : GetPath(packageId);
        if (path is not null) File.Delete(path);
    }

    public static bool ContainsNginxExecutable(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Any(entry => IsSafeEntry(entry.FullName)
            && entry.FullName.EndsWith("/nginx.exe", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSafeEntry(string entryName) => !Path.IsPathRooted(entryName)
        && !entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");

    private static bool IsSymbolicLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return true; }
    }
}
