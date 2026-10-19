using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Stages bundled or user-selected GEO data under fixed private names. Mihomo only receives this
/// managed directory through <c>-d</c>; it never receives an arbitrary UI path.
/// </summary>
public sealed class MihomoGeoDataService(
    IProxyPlatformPaths paths,
    IProxyDiagnosticLogStore? diagnostics = null,
    string? bundledDataDirectory = null,
    IReadOnlyDictionary<string, string>? bundledFileHashes = null) : IProxyGeoDataService
{
    private const long MaximumBytes = MihomoGeoDataFiles.MaximumBytes;
    private const int CopyAttempts = 3;
    private const string PrimaryFileName = MihomoGeoDataFiles.PrimaryFile;
    // The declared GEO set and its digests live in one place so the managed runtime places exactly
    // the artifacts this service verifies; neither side may widen the set on its own.
    private static readonly string[] BundledFileNames = MihomoGeoDataFiles.Bundled.Select(file => file.Name).ToArray();
    private static readonly IReadOnlyDictionary<string, string> BundledFileHashes =
        MihomoGeoDataFiles.Bundled.ToDictionary(file => file.Name, file => file.Sha256, StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<ProxyGeoDataDto> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(ManagedFilePath(PrimaryFileName));
            return Task.FromResult(info.Exists && info.Length is > 0 and <= MaximumBytes
                ? new ProxyGeoDataDto(true, info.Length)
                : new ProxyGeoDataDto(false));
        }
        catch (IOException) { return Task.FromResult(new ProxyGeoDataDto(false)); }
        catch (UnauthorizedAccessException) { return Task.FromResult(new ProxyGeoDataDto(false)); }
    }

    public async Task<string?> EnsureBundledAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sourceDirectory = bundledDataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Mihomo", "GeoData");
            var invalidFiles = BundledFileNames.Where(name => !IsSafeBundledFile(Path.Combine(sourceDirectory, name))).ToArray();
            if (invalidFiles.Length > 0)
            {
                await WriteDiagnosticAsync("warning", "Packaged Mihomo GEO data failed existence, size, or SHA-256 verification: " + string.Join(", ", invalidFiles), cancellationToken);
                return ProxyProblemCodes.GeodataUnavailable;
            }

            var directory = paths.GetEngineDataDirectory(MihomoEngine.Id);
            Directory.CreateDirectory(directory);
            MakePrivateDirectory(directory);
            foreach (var fileName in BundledFileNames)
            {
                var destination = ManagedFilePath(fileName);
                // The existing UI explicitly lets an administrator stage a Server-local
                // geoip.metadb. Keep that intentional override.  The other artifacts are
                // immutable verified copies, so do not replace an identical file while Mihomo
                // is using it: Windows can hold its database files open without delete sharing.
                if (fileName == PrimaryFileName && IsExistingManagedFile(destination)) continue;
                if (fileName != PrimaryFileName && IsSafeBundledFile(destination)) continue;
                try { await CopyAtomicallyAsync(Path.Combine(sourceDirectory, fileName), destination, cancellationToken); }
                catch (IOException exception) { throw new GeoDataStagingException(fileName, exception); }
            }

            await WriteDiagnosticAsync("info", "Bundled GEO data was staged for managed Mihomo.", cancellationToken);
            return null;
        }
        catch (GeoDataStagingException exception)
        {
            await WriteDiagnosticAsync("warning", $"Bundled GEO data copy failed: {exception.FileName} ({exception.InnerException!.GetType().Name}, HRESULT 0x{exception.InnerException.HResult:X8}).", cancellationToken);
            return ProxyProblemCodes.GeodataUnavailable;
        }
        catch (IOException exception)
        {
            await WriteDiagnosticAsync("warning", $"Bundled GEO data staging failed ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).", cancellationToken);
            return ProxyProblemCodes.GeodataUnavailable;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync("warning", "The RelaxKonOS Server service account cannot stage bundled GEO data.", cancellationToken);
            return ProxyProblemCodes.PrivilegedOperationUnavailable;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ConfigureFromServerFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!TrySafeSourcePath(filePath, out var source)) return ProxyProblemCodes.GeodataInvalid;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = paths.GetEngineDataDirectory(MihomoEngine.Id);
            Directory.CreateDirectory(directory);
            MakePrivateDirectory(directory);
            var temporary = Path.Combine(directory, ".geoip-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                if (input.Length is <= 0 or > MaximumBytes) return ProxyProblemCodes.GeodataInvalid;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await CopyLimitedAsync(input, output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                MakePrivateFile(temporary);
                File.Move(temporary, ManagedFilePath(PrimaryFileName), overwrite: true);
                MakePrivateFile(ManagedFilePath(PrimaryFileName));
                await WriteDiagnosticAsync("info", "A Server-local GeoIP database was staged for managed Mihomo.", cancellationToken);
                return null;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (IOException exception)
        {
            await WriteDiagnosticAsync("warning", $"The selected Server-local GeoIP database could not be staged ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).", cancellationToken);
            return ProxyProblemCodes.GeodataInvalid;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync("warning", "The RelaxKonOS Server service account cannot stage the selected GeoIP database.", cancellationToken);
            return ProxyProblemCodes.PrivilegedOperationUnavailable;
        }
        finally { _gate.Release(); }
    }

    private string ManagedFilePath(string fileName) => Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), fileName);

    private bool IsSafeBundledFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || !(bundledFileHashes ?? BundledFileHashes).TryGetValue(info.Name, out var expectedHash)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return MihomoGeoDataFiles.DigestMatches(expectedHash, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsExistingManagedFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= MaximumBytes && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= CopyAttempts; attempt++)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".geodata-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await CopyLimitedAsync(input, output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                MakePrivateFile(temporary);
                File.Move(temporary, destination, overwrite: true);
                MakePrivateFile(destination);
                return;
            }
            catch (IOException) when (attempt < CopyAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
    private static bool TrySafeSourcePath(string value, out string path)
    {
        path = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            if (!path.EndsWith(".metadb", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task CopyLimitedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            total += read;
            if (total > MaximumBytes) throw new IOException("The GeoIP database exceeds the maximum allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private async Task WriteDiagnosticAsync(string level, string message, CancellationToken cancellationToken)
    {
        if (diagnostics is not null) await diagnostics.WriteAsync(level, message, cancellationToken);
    }
    private static void MakePrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void MakePrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }

    private sealed class GeoDataStagingException(string fileName, IOException innerException) : IOException(innerException.Message, innerException)
    {
        public string FileName { get; } = fileName;
    }
}
