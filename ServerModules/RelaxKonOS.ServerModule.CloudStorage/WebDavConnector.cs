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
        response.EnsureSuccessStatusCode();
    }

    internal async Task UploadAsync(string path, Stream stream, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Content = new StreamContent(stream);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<(string Path, CloudEntryDto Value)[]> PropertiesAsync(string path, string depth, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpMethod("PROPFIND"), path, depth, ct);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException("cloud-storage-webdav", null, response.StatusCode);
        var document = await CloudTransport.ReadXmlAsync(response, ct);
        if (document.Root?.Name != Dav + "multistatus") throw new InvalidDataException("cloud-storage-webdav");
        var entries = new List<(string, CloudEntryDto)>();
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
                CloudTransport.Date((string?)prop.Element(Dav + "getlastmodified")))));
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
                <propfind xmlns="DAV:"><prop><resourcetype/><getcontentlength/><getlastmodified/></prop></propfind>
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
