using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Fetches one signed catalog envelope from an explicitly configured HTTPS URI.</summary>
internal static class ModulePackageCatalogFetcher
{
    private const int Limit = 6 * 1024 * 1024;
    internal static async Task<byte[]> FetchAsync(string value, HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ModulePackageException("module-catalog-uri-invalid");
        using var ownedHandler = handler is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
        using var client = new HttpClient(handler ?? ownedHandler!, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ModulePackageException("module-catalog-fetch-failed");
        if (response.Content.Headers.ContentLength is > Limit) throw new ModulePackageException("module-catalog-size");
        using var buffer = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var chunk = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > Limit) throw new ModulePackageException("module-catalog-size");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw new ModulePackageException("module-catalog-size");
        return buffer.ToArray();
    }
}
