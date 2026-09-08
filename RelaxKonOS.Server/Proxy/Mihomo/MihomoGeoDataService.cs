using System.Security.Cryptography;
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
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const int CopyAttempts = 3;
    private const string PrimaryFileName = "geoip.metadb";
    private static readonly string[] BundledFileNames =
    [
        "geoip.metadb",
        "geoip.dat",
        "geosite.dat",
        "country.mmdb",
        "GeoLite2-ASN.mmdb",
    ];
    // SHA-256 values of the artifacts committed in Assets/Mihomo/GeoData. Do not accept a
    // partially downloaded or replaced payload merely because its name and length look valid.
    private static readonly IReadOnlyDictionary<string, string> BundledFileHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["country.mmdb"] = "FE721D5E47D320B2A23DB4EAFDDB796A22026EF01899BBE7007FC0274016E5F4",
        ["geoip.dat"] = "0D5D2BA0C5A5C58027FD1347A6AFD57C9470799B6BB3CBC274FD4657ED8DE382",
        ["geoip.metadb"] = "91EF340938FF44A94FF8E5D8D8BD7E8D7DAD9D9E3C4ECEA9E160DD95E6A9916B",
        ["GeoLite2-ASN.mmdb"] = "93456017EEF970E7E60AB66312402B2130BB233AF792A5AA30B2FF4DE854C5CF",
        ["geosite.dat"] = "665FAD6D83E9F3CF28EC7200D2812280508FBBF07983818A33CAF90514AB6F17",
    };
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
            if (!BundledFileNames.All(name => IsSafeBundledFile(Path.Combine(sourceDirectory, name))))
            {
                await WriteDiagnosticAsync("warning", "The packaged Mihomo GEO data is missing, invalid, or exceeds its size limit.", cancellationToken);
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
            await WriteDiagnosticAsync("warning", "Bundled GEO data could not be staged for managed Mihomo: " + exception.FileName + " (" + exception.InnerException!.GetType().Name + ").", cancellationToken);
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
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyLimitedAsync(input, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
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
        catch (IOException)
        {
            await WriteDiagnosticAsync("warning", "The selected Server-local GeoIP database could not be staged.", cancellationToken);
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
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.Ordinal);
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
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyLimitedAsync(input, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
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
