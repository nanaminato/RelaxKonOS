using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.Developer;

internal sealed partial class OfficialDesktopPackages
{
    internal const string SourceFile = "catalog-source.json";
    private static readonly HttpClient OfficialHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal string BundledPath(string id) => Path.Combine(directory, id + ".roapp");

    internal async Task<IReadOnlyList<OfficialDesktopRelease>> BrowseOnlineAsync(CancellationToken ct)
    {
        var catalog = await ReadSourceAsync(ct);
        if (catalog is null) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(catalog, deadline.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, OfficialDesktopCatalogRules.MaximumCatalogBytes, deadline.Token);
        buffer.Position = 0;
        var index = await JsonSerializer.DeserializeAsync<OfficialDesktopCatalogDocument>(buffer, Json, deadline.Token);
        if (index is not { SchemaVersion: OfficialDesktopCatalogRules.SchemaVersion, Releases.Length: <= OfficialDesktopCatalogRules.MaximumReleases })
            throw new InvalidDataException("Invalid official desktop catalog.");
        var identities = new HashSet<(string, string)>();
        var releases = new List<OfficialDesktopRelease>();
        foreach (var entry in index.Releases)
        {
            if (!OfficialDesktopCatalogRules.IsValid(entry) || !identities.Add((entry.AppId, entry.Version)))
                throw new InvalidDataException("Invalid official desktop release.");
            releases.Add(new(entry.AppId, entry.Version, entry.DisplayName, entry.Description, entry.Sha256, entry.Bytes, catalog));
        }
        return releases;
    }

    internal async Task DownloadAsync(OfficialDesktopRelease release, Stream destination, CancellationToken ct)
    {
        await RequireOnlineSourceAsync(release, ct);
        var artifact = new Uri(release.CatalogUri!, OfficialDesktopCatalogRules.ArtifactPath(release.AppId, release.Version));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await GetAsync(artifact, deadline.Token);
        if (response.Content.Headers.ContentLength is { } bytes && bytes != release.Bytes)
            throw new InvalidDataException("Official desktop package length changed.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        if (await CopyBoundedAsync(input, destination, release.Bytes, deadline.Token) != release.Bytes)
            throw new InvalidDataException("Official desktop package length changed.");
    }

    internal async Task VerifyDownloadAsync(OfficialDesktopRelease release, Stream package, CancellationToken ct)
    {
        await RequireOnlineSourceAsync(release, ct);
        if (package.Length != release.Bytes) throw new InvalidDataException("Official desktop package length changed.");
        await AppPackageInstallerService.VerifyFrozenAsync(package, release.Sha256, ct);
    }

    private async Task RequireOnlineSourceAsync(OfficialDesktopRelease release, CancellationToken ct)
    {
        if (release.CatalogUri is null || release.CatalogUri != await ReadSourceAsync(ct)
            || !IsExternalOfficialId(release.AppId) || !OfficialDesktopCatalogRules.ValidVersion(release.Version)
            || release.Bytes is <= 0 or > OfficialDesktopCatalogRules.MaximumPackageBytes)
            throw new InvalidOperationException("The preconfigured official desktop source changed.");
    }

    private async Task<Uri?> ReadSourceAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, SourceFile);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096 || new[] { directory, path }.Any(p =>
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Invalid official desktop source configuration.");
        await using var input = File.OpenRead(path);
        if (input.Length > 4096) throw new InvalidDataException("Invalid official desktop source configuration.");
        var config = await JsonSerializer.DeserializeAsync<StoreSource>(input, Json, ct);
        if (!Uri.TryCreate(config?.CatalogUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
            throw new InvalidDataException("The official desktop catalog must be a fixed HTTPS catalog.json URL.");
        return uri;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await (client ?? OfficialHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.OK && response.RequestMessage?.RequestUri == uri)
            return response;
        response.Dispose();
        throw new HttpRequestException("The official desktop source did not return the requested file.");
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        long total = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > maximum) throw new InvalidDataException("The official desktop download exceeds its allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    private sealed record StoreSource(string CatalogUri);
}

internal sealed record OfficialDesktopRelease(string AppId, string Version, string DisplayName, string? Description,
    string Sha256, long Bytes, Uri? CatalogUri);

internal sealed record OfficialDesktopCatalog(IReadOnlyList<OfficialDesktopRelease> Releases, bool OnlineUnavailable);
