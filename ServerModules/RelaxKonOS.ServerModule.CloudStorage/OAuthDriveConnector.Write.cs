using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed partial class OAuthDriveConnector
{
    internal async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        if (protocol == "google-drive")
        {
            var (parent, name) = GoogleDestination(path);
            await EnsureGoogleNameAvailableAsync(parent, name, ct);
            using var request = GoogleRequest(HttpMethod.Post, new Uri(Google),
                JsonContent.Create(new { name, parents = new[] { parent }, mimeType = "application/vnd.google-apps.folder" }));
            using var client = transport(request.RequestUri!, false);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        else
        {
            await BaiduFormAsync("create", new()
            {
                ["path"] = "/" + path.TrimEnd('/'), ["isdir"] = "1", ["rtype"] = "0", ["size"] = "0",
            }, ct);
        }
    }

    internal Task UploadAsync(string path, Stream stream, CancellationToken ct) =>
        protocol == "google-drive" ? UploadGoogleAsync(path, stream, ct) : UploadBaiduAsync(path, stream, ct);

    private async Task UploadGoogleAsync(string path, Stream stream, CancellationToken ct)
    {
        var (parent, name) = GoogleDestination(path);
        await EnsureGoogleNameAvailableAsync(parent, name, ct);
        var endpoint = new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
        using var client = transport(endpoint, false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var init = GoogleRequest(HttpMethod.Post, endpoint, JsonContent.Create(new { name, parents = new[] { parent } }));
        var total = stream.Length;
        init.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
        init.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        using var started = await client.SendAsync(init, HttpCompletionOption.ResponseHeadersRead, ct);
        started.EnsureSuccessStatusCode();
        if (started.Headers.Location is not { IsAbsoluteUri: true } session
            || session.Scheme != "https" || session.IdnHost != endpoint.IdnHost || session.Port != endpoint.Port
            || session.UserInfo.Length != 0 || session.AbsolutePath != endpoint.AbsolutePath)
            throw new InvalidDataException("cloud-google-upload-origin");

        var buffer = new byte[(int)Math.Min(total, FileUploadProtocol.DefaultChunkSize)];
        long offset = 0;
        do
        {
            var size = (int)Math.Min(buffer.Length, total - offset);
            stream.Position = offset;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct);
            using var content = new ByteArrayContent(buffer, 0, size);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (size > 0) content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + size - 1, total);
            using var put = GoogleRequest(HttpMethod.Put, session, content);
            using var response = await client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode != 308)
            {
                response.EnsureSuccessStatusCode();
                if (offset + size != total) throw new InvalidDataException("cloud-google-upload-length");
                return;
            }

            if (!response.Headers.TryGetValues("Range", out var ranges)
                || !RangeHeaderValue.TryParse(ranges.SingleOrDefault(), out var range)
                || range.Unit != "bytes" || range.Ranges.Count != 1 || range.Ranges.Single() is not { From: 0, To: { } last }
                || last < offset || last >= offset + size || last >= total - 1)
                throw new InvalidDataException("cloud-google-upload-offset");
            offset = last + 1;
        } while (offset < total);
    }

    private async Task EnsureGoogleNameAvailableAsync(string parent, string name, CancellationToken ct)
    {
        var quoted = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var result = await FetchAsync(Query(Google, new()
        {
            ["q"] = $"'{parent}' in parents and trashed = false and name = '{quoted}'",
            ["fields"] = "files(id)", ["pageSize"] = "1",
        }), ct);
        if (Items(result, "files").Any())
            throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
    }

    private static (string Parent, string Name) GoogleDestination(string path)
    {
        var segments = path.TrimEnd('/').Split('/');
        if (segments.Length > 2) throw new ArgumentException("directory");
        return (segments.Length == 1 ? "root" : GoogleId(segments[0]), segments[^1]);
    }

    private HttpRequestMessage GoogleRequest(HttpMethod method, Uri uri, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task UploadBaiduAsync(string path, Stream stream, CancellationToken ct)
    {
        const int blockSize = 4 * 1024 * 1024;
        var destination = "/" + path;
        var total = stream.Length;
        var buffer = new byte[(int)Math.Min(total, blockSize)];
        var hashes = new List<string>();
        do
        {
            var length = (int)Math.Min(buffer.Length, total - stream.Position);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
            hashes.Add(Convert.ToHexStringLower(MD5.HashData(buffer.AsSpan(0, length))));
        } while (stream.Position < total);
        var fields = new Dictionary<string, string>
        {
            ["path"] = destination, ["isdir"] = "0", ["rtype"] = "0", ["size"] = total.ToString(CultureInfo.InvariantCulture),
            ["block_list"] = JsonSerializer.Serialize(hashes), ["autoinit"] = "1",
        };
        var staged = await BaiduFormAsync("precreate", fields, ct);
        if (Number(staged, "return_type") == 2) return;
        var uploadId = Read(staged, "uploadid");
        if (string.IsNullOrWhiteSpace(uploadId) || uploadId.Length > 4096
            || !staged.TryGetProperty("block_list", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("cloud-baidu-upload-session");

        foreach (var part in blocks.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetInt32(out var index) || index < 0 || index >= hashes.Count)
                throw new InvalidDataException("cloud-baidu-upload-part");
            stream.Position = (long)index * blockSize;
            var length = (int)Math.Min(blockSize, total - stream.Position);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
            var uri = Query("https://d.pcs.baidu.com/rest/2.0/pcs/superfile2", new()
            {
                ["method"] = "upload", ["type"] = "tmpfile", ["path"] = destination, ["uploadid"] = uploadId,
                ["partseq"] = index.ToString(CultureInfo.InvariantCulture), ["access_token"] = token,
            });
            using var body = new MultipartFormDataContent();
            body.Add(new ByteArrayContent(buffer, 0, length), "file", "file");
            var response = await BaiduPostAsync(uri, body, ct);
            if (!string.Equals(Read(response, "md5"), hashes[index], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("cloud-baidu-upload-hash");
        }

        fields.Remove("autoinit");
        fields["uploadid"] = uploadId;
        await BaiduFormAsync("create", fields, ct);
    }

    private async Task<JsonElement> BaiduFormAsync(string method, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(fields);
        return await BaiduPostAsync(BaiduUri("file", new() { ["method"] = method }), form, ct);
    }

    private async Task<JsonElement> BaiduPostAsync(Uri uri, HttpContent body, CancellationToken ct)
    {
        using var client = transport(uri, false);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = body };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var result = await JsonAsync(response, ct);
        if (Number(result, "errno") is { } code && code == 0) return result;
        if (Number(result, "errno") == -8)
            throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
        throw new InvalidDataException("cloud-baidu-write");
    }
}
