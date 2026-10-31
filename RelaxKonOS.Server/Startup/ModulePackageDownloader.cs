using System.Security.Cryptography;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>Downloads one exact release from an accepted signed catalog into the staging upload area.</summary>
internal static class ModulePackageDownloader
{
    private const long Limit = 1L << 30;
    internal static async Task<ServerModulePackageUploadDto> DownloadAsync(string contentRoot, IConfiguration configuration,
        IHostEnvironment environment, string sourceId, string releaseId, DateTimeOffset now,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (!ModulePackageHostConfiguration.Identifier(sourceId) || !ModulePackageHostConfiguration.Identifier(releaseId)
            || !Path.IsPathFullyQualified(contentRoot) || !Directory.Exists(contentRoot))
            throw new ModulePackageException("module-store-download-request-invalid");
        var serviceId = ModuleManagementStore.ReadRecoveryReport(contentRoot).ServiceId;
        var policy = ModulePackageHostConfiguration.Source(configuration, sourceId);
        var context = ModulePackageHostConfiguration.Context(serviceId, configuration, environment);
        var document = ModulePackageCatalogStore.Read(contentRoot, serviceId);
        var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy, context, now);
        var release = verified.Catalog.Releases.SingleOrDefault(item => item.Manifest.ReleaseId == releaseId)
            ?? throw new ModulePackageException("module-package-release-not-found");
        ModulePackageCatalogStore.EnsureNotRevoked(document, releaseId, release.ArchiveSha256);
        if (!Uri.TryCreate(release.DownloadUri, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !release.DownloadUri.StartsWith(policy.ArtifactOrigin, StringComparison.Ordinal))
            throw new ModulePackageException("module-store-download-origin-invalid");

        var directory = Path.Combine(contentRoot, "data", "module-management", "staging", "uploads");
        Directory.CreateDirectory(directory);
        var uploadId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, uploadId + ".rkmodule");
        var temporary = path + ".tmp";
        try
        {
            using var ownedHandler = handler is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
            using var client = new HttpClient(handler ?? ownedHandler!, disposeHandler: false)
            { Timeout = TimeSpan.FromMinutes(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ModulePackageException("module-store-download-failed");
            if (response.Content.Headers.ContentLength is > Limit) throw new ModulePackageException("module-store-download-size");
            long bytes = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            65536, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    bytes += read;
                    if (bytes > Limit) throw new ModulePackageException("module-store-download-size");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (bytes != release.ArchiveBytes) throw new ModulePackageException("module-store-download-size");
            if (digest != release.ArchiveSha256) throw new ModulePackageException("module-store-download-digest");
            File.Move(temporary, path);
            return new ServerModulePackageUploadDto(uploadId, bytes, digest);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
