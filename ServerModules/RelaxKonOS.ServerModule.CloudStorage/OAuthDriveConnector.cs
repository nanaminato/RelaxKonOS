using System.Globalization;
using System.Net;
using System.Text.Json;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed partial class OAuthDriveConnector(string token, Func<Uri, bool, HttpClient> transport)
{
    private const string Baidu = "https://pan.baidu.com/rest/2.0/xpan/";
    private const int PageSize = 200;

    internal static bool Supports(string protocol) => protocol == "baidu-netdisk";

    internal static async Task<(string Access, string Refresh, DateTimeOffset Expires)> RefreshAsync(
        CloudConnectionRequest settings, Func<Uri, bool, HttpClient> transport, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = settings.SessionToken!,
            ["client_id"] = settings.UserName, ["client_secret"] = settings.Secret,
        };
        var uri = Query("https://openapi.baidu.com/oauth/2.0/token", form);
        using var client = transport(uri, false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var failure = await ReadJsonAsync(response, ct);
                ThrowIfRefreshRejected(failure);
            }
            catch (JsonException) { }
        }
        var root = await JsonAsync(response, ct);
        ThrowIfRefreshRejected(root);
        var access = Read(root, "access_token");
        if (string.IsNullOrEmpty(access) || access.Length > 8192 || access.Any(char.IsControl)
            || Number(root, "expires_in") is not > 0)
            throw new InvalidDataException("cloud-oauth-token");
        var refresh = Read(root, "refresh_token") ?? settings.SessionToken!;
        if (refresh.Length is 0 or > 8192 || refresh.Any(char.IsControl))
            throw new InvalidDataException("cloud-oauth-refresh");
        return (access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Min(Number(root, "expires_in")!.Value, 86400)));
    }

    private static void ThrowIfRefreshRejected(JsonElement result)
    {
        if (Read(result, "error") is "invalid_grant" or "invalid_client" or "invalid_refresh_token")
            throw new HttpRequestException("cloud-oauth-refresh-rejected", null, HttpStatusCode.Unauthorized);
    }

    internal async Task<CloudDirectoryDto> ListAsync(string reference, string? cursor, CancellationToken ct)
    {
        if (reference.StartsWith("id:", StringComparison.Ordinal)) throw new ArgumentException("directory");
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || offset > int.MaxValue - PageSize))
            throw new ArgumentException("cursor");
        var directory = "/" + reference.TrimEnd('/');
        var result = await FetchAsync(BaiduUri("file", new()
        {
            ["method"] = "list", ["dir"] = directory, ["start"] = offset.ToString(CultureInfo.InvariantCulture),
            ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture), ["order"] = "name",
        }), ct);
        var entries = Items(result, "list").ToArray();
        return new(entries.Select(BaiduEntry).ToArray(), entries.Length == PageSize
            ? (offset + PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    internal async Task<CloudEntryDto?> InfoAsync(string path, CancellationToken ct)
    {
        if (path.Length == 0 || path.EndsWith('/'))
            return new(CloudTransport.Reference(path), path.TrimEnd('/').Split('/')[^1], true, null, null);
        var item = await BaiduInfoAsync(path, download: false, ct);
        return item is { } entry ? BaiduEntry(entry) : null;
    }

    internal async Task DownloadAsync(string reference, Func<string, Stream, long?, Task> send, CancellationToken ct)
    {
        var item = await BaiduInfoAsync(reference, download: true, ct)
            ?? throw new HttpRequestException("cloud-file-missing", null, HttpStatusCode.NotFound);
        var name = BaiduEntry(item).Name;
        if (!Uri.TryCreate(Read(item, "dlink"), UriKind.Absolute, out var link) || !AllowedBaiduDownload(link))
            throw new InvalidDataException("cloud-baidu-download-origin");
        var uri = new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(link.AbsoluteUri, "access_token", token));

        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var client = transport(uri, false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("pan.baidu.com");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308
                && response.Headers.Location is { } location)
            {
                var next = new Uri(uri, location);
                if (!AllowedBaiduDownload(next)) throw new InvalidDataException("cloud-baidu-download-origin");
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await send(name, body, response.Content.Headers.ContentLength);
            return;
        }
        throw new InvalidDataException("cloud-download-redirect-limit");
    }

    internal static bool AllowedBaiduDownload(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0
        && (uri.IdnHost is "pan.baidu.com" or "pcs.baidu.com" or "baidupcs.com"
            || uri.IdnHost.EndsWith(".pcs.baidu.com", StringComparison.Ordinal)
            || uri.IdnHost.EndsWith(".baidupcs.com", StringComparison.Ordinal));

    private async Task<JsonElement?> BaiduInfoAsync(string path, bool download, CancellationToken ct)
    {
        if (!path.StartsWith("id:", StringComparison.Ordinal)
            || !long.TryParse(path[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("file");
        var result = await FetchAsync(BaiduUri("multimedia", new()
        {
            ["method"] = "filemetas", ["fsids"] = $"[{id.ToString(CultureInfo.InvariantCulture)}]", ["dlink"] = download ? "1" : "0",
        }), ct);
        foreach (var item in Items(result, "list"))
            if (Number(item, "fs_id") == id) return item;
        return null;
    }

    private Uri BaiduUri(string endpoint, Dictionary<string, string> fields)
    {
        fields["access_token"] = token;
        return Query(Baidu + endpoint, fields);
    }

    private async Task<JsonElement> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var client = transport(uri, false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var root = await JsonAsync(response, ct);
        if (Number(root, "errno") != 0)
            throw new InvalidDataException("cloud-baidu-api");
        return root;
    }

    private static Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return ReadJsonAsync(response, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("cloud-oauth-response-size");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static CloudEntryDto BaiduEntry(JsonElement item)
    {
        var path = Read(item, "path") ?? throw new InvalidDataException("cloud-baidu-path");
        var relative = path.Trim('/');
        var directory = Number(item, "isdir") == 1;
        if (!CloudTransport.ValidPath(relative) || relative.Length == 0 || Number(item, "fs_id") is not > 0)
            throw new InvalidDataException("cloud-baidu-entry");
        var reference = directory ? relative + "/" : "id:" + Number(item, "fs_id")!.Value.ToString(CultureInfo.InvariantCulture);
        var seconds = Number(item, "server_mtime");
        return new(CloudTransport.Reference(reference), Read(item, "server_filename") ?? Read(item, "filename") ?? relative.Split('/')[^1],
            directory, directory ? null : Number(item, "size"),
            seconds is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null);
    }

    private static Uri Query(string url, Dictionary<string, string> parameters) => new(
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url,
            parameters.Where(pair => pair.Value.Length > 0).Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value))));

    private static string? Read(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var value)
        && (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            || value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number)) ? number : null;

    private static JsonElement.ArrayEnumerator Items(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : default;
}
