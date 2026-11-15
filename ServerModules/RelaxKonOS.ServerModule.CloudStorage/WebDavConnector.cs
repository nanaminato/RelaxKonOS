using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed class WebDavConnector(CloudConnectionRequest connection, HttpClient http)
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Uri _root = new(connection.Endpoint.TrimEnd('/') + "/");
    private const int PageSize = 200;

    internal async Task<CloudDirectoryDto> ListAsync(string path, string? cursor, CancellationToken ct)
    {
        if (path.Length > 0 && !path.EndsWith('/')) throw new ArgumentException("directory");
        if (cursor is not null && (!int.TryParse(cursor, out var offset) || offset < 0 || offset > 100_000))
            throw new ArgumentException("cursor");
        var skip = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var entries = await PropertiesAsync(path, "1", ct);
        var children = entries.Where(entry => entry.Path != path
            && entry.Path.StartsWith(path, StringComparison.Ordinal)
            && !entry.Path[path.Length..].TrimEnd('/').Contains('/'))
            .DistinctBy(entry => entry.Path, StringComparer.Ordinal)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Skip(skip).Take(PageSize + 1).ToArray();
        return new(children.Take(PageSize).Select(entry => entry.Value).ToArray(),
            children.Length > PageSize ? (skip + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    internal async Task<CloudEntryDto?> InfoAsync(string path, CancellationToken ct) =>
        (await PropertiesAsync(path, "0", ct)).FirstOrDefault(entry => entry.Path == path).Value;

    internal Task<HttpResponseMessage> DownloadAsync(string path, CancellationToken ct)
    {
        if (path.Length == 0 || path.EndsWith('/')) throw new ArgumentException("file");
        return SendAsync(HttpMethod.Get, path, null, ct);
    }

    internal async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        using var request = CreateRequest(new HttpMethod("MKCOL"), path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed && await InfoAsync(path, ct) is not null)
            throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
        response.EnsureSuccessStatusCode();
    }

    internal async Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        using var request = CreateRequest(new HttpMethod("MOVE"), source);
        request.Headers.Add("Destination", new Uri(_root.AbsoluteUri + CloudTransport.EscapePath(destination)).AbsoluteUri);
        request.Headers.Add("Overwrite", "F");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureMutationSucceeded(response);
    }

    internal async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, ct);
        EnsureMutationSucceeded(response);
    }

    private static void EnsureMutationSucceeded(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.MultiStatus)
            throw new InvalidDataException("cloud-webdav-mutation-partial");
        response.EnsureSuccessStatusCode();
    }

    internal async Task UploadAsync(string path, Stream stream, CancellationToken ct, bool replace = false)
    {
        if (replace)
        {
            await ReplaceAsync(path, stream, ct);
            return;
        }
        var staging = path[..(path.LastIndexOf('/') + 1)] + ".relaxkonos-upload-" + Guid.NewGuid().ToString("N");
        using var put = CreateRequest(HttpMethod.Put, staging);
        put.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        put.Content = new StreamContent(stream);
        var cleanupNeeded = true;
        try
        {
            using var uploaded = await http.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
            if (uploaded.StatusCode == HttpStatusCode.PreconditionFailed) cleanupNeeded = false;
            uploaded.EnsureSuccessStatusCode();
            using var move = CreateRequest(new HttpMethod("MOVE"), staging);
            move.Headers.Add("Destination", new Uri(_root.AbsoluteUri + CloudTransport.EscapePath(path)).AbsoluteUri);
            move.Headers.Add("Overwrite", "F");
            using var response = await http.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            cleanupNeeded = false;
        }
        finally
        {
            if (cleanupNeeded)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { using var cleanup = await SendAsync(HttpMethod.Delete, staging, null, deadline.Token); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    private async Task ReplaceAsync(string path, Stream stream, CancellationToken ct)
    {
        var target = (await PropertiesAsync(path, "0", ct)).FirstOrDefault(entry => entry.Path == path);
        if (target.Value is not { IsDirectory: false } || !EntityTagHeaderValue.TryParse(target.ETag, out var etag)
            || etag.IsWeak || etag == EntityTagHeaderValue.Any)
            throw new HttpRequestException("cloud-storage-replacement-requires-file-etag", null, HttpStatusCode.PreconditionFailed);
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Headers.IfMatch.Add(etag);
        request.Content = new StreamContent(stream);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureMutationSucceeded(response);
    }

    private async Task<(string Path, CloudEntryDto Value, string? ETag)[]> PropertiesAsync(string path, string depth, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpMethod("PROPFIND"), path, depth, ct);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException("cloud-storage-webdav", null, response.StatusCode);
        var document = await CloudTransport.ReadXmlAsync(response, ct);
        if (document.Root?.Name != Dav + "multistatus") throw new InvalidDataException("cloud-storage-webdav");
        var entries = new List<(string, CloudEntryDto, string?)>();
        foreach (var item in document.Root.Elements(Dav + "response"))
        {
            var href = (string?)item.Element(Dav + "href");
            if (href is null || !Uri.TryCreate(_root, href, out var uri)
                || uri.Scheme != _root.Scheme || uri.IdnHost != _root.IdnHost || uri.Port != _root.Port
                || !uri.AbsolutePath.StartsWith(_root.AbsolutePath, StringComparison.Ordinal)) continue;
            var relative = string.Join('/', uri.AbsolutePath[_root.AbsolutePath.Length..].Split('/')
                .Select(segment => Uri.UnescapeDataString(segment)));
            if (!CloudTransport.ValidPath(relative)
                || uri.AbsolutePath[_root.AbsolutePath.Length..].Contains("%2f", StringComparison.OrdinalIgnoreCase)) continue;
            var prop = item.Elements(Dav + "propstat")
                .FirstOrDefault(status => ((string?)status.Element(Dav + "status"))?.Split(' ').ElementAtOrDefault(1) == "200")
                ?.Element(Dav + "prop");
            if (prop is null) continue;
            var directory = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            if (directory && relative.Length > 0 && !relative.EndsWith('/')) relative += "/";
            if (!directory && (relative.Length == 0 || relative.EndsWith('/'))) continue;
            var name = relative.TrimEnd('/').Split('/')[^1];
            entries.Add((relative, new(CloudTransport.Reference(relative), name, directory,
                directory ? null : CloudTransport.Size((string?)prop.Element(Dav + "getcontentlength")),
                CloudTransport.Date((string?)prop.Element(Dav + "getlastmodified"))),
                (string?)prop.Element(Dav + "getetag")));
        }
        return entries.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? depth, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (depth is not null)
        {
            request.Headers.Add("Depth", depth);
            request.Content = new StringContent("""
                <propfind xmlns="DAV:"><prop><resourcetype/><getcontentlength/><getlastmodified/><getetag/></prop></propfind>
                """, Encoding.UTF8, "application/xml");
        }
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_root.AbsoluteUri + CloudTransport.EscapePath(path)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.UserName + ":" + connection.Secret)));
        return request;
    }
}
