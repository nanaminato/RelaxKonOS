using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed partial class OAuthDriveConnector
{
    internal async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        await BaiduFormAsync("create", new()
        {
            ["path"] = "/" + path.TrimEnd('/'), ["isdir"] = "1", ["rtype"] = "0", ["size"] = "0",
        }, ct);
    }

    internal async Task UploadAsync(string path, Stream stream, CancellationToken ct)
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
