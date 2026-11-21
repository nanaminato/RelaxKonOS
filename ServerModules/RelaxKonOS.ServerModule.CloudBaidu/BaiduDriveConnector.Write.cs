using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudBaidu;

internal sealed partial class BaiduDriveConnector
{
    internal async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        await BaiduFormAsync("create", new()
        {
            ["path"] = "/" + path.TrimEnd('/'), ["isdir"] = "1", ["rtype"] = "0", ["size"] = "0",
        }, ct);
    }

    internal Task DeleteAsync(string path, CancellationToken ct) =>
        MutateAsync("delete", JsonSerializer.Serialize(new[] { "/" + path.TrimEnd('/') }), ct);

    internal async Task<string> ResolveMutationPathAsync(string reference, CancellationToken ct)
    {
        if (!reference.StartsWith("id:", StringComparison.Ordinal)) return reference;
        var item = await BaiduInfoAsync(reference, download: false, ct) ?? throw new KeyNotFoundException();
        if (BaiduEntry(item).IsDirectory) throw new ArgumentException("file reference");
        return Read(item, "path")!.Trim('/');
    }

    internal Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        var path = destination.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        return MutateAsync("move", JsonSerializer.Serialize(new[]
        {
            new { path = "/" + source.TrimEnd('/'), dest = "/" + path[..(separator + 1)].TrimEnd('/'),
                newname = path[(separator + 1)..], ondup = "fail" },
        }), ct);
    }

    private async Task MutateAsync(string operation, string fileList, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["async"] = "0", ["filelist"] = fileList, ["ondup"] = "fail",
        });
        var result = await BaiduPostAsync(BaiduUri("file", new() { ["method"] = "filemanager", ["opera"] = operation }), form, ct);
        if (Number(result, "taskid") is > 0 || !result.TryGetProperty("info", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1)
            throw new InvalidDataException("cloud-baidu-mutation-unconfirmed");
        var error = Number(items[0], "errno");
        if (error == -8) throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
        if (error != 0) throw new InvalidDataException("cloud-baidu-mutation-failed");
    }

    internal async Task UploadAsync(string path, Stream stream, CancellationToken ct, bool replace = false)
    {
        if (replace && !await HasFileAsync(path, ct))
            throw new HttpRequestException("cloud-storage-replacement-requires-file", null, HttpStatusCode.PreconditionFailed);
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
            ["path"] = destination, ["isdir"] = "0", ["rtype"] = replace ? "3" : "0",
            ["size"] = total.ToString(CultureInfo.InvariantCulture),
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
            var file = new ByteArrayContent(buffer, 0, length);
            file.Headers.ContentType = new("application/octet-stream");
            body.Add(file, "file", "file");
            var response = await PostJsonAsync(uri, body, ct);
            if (response.ValueKind != JsonValueKind.Object
                || response.TryGetProperty("errno", out _) && Number(response, "errno") != 0
                || response.TryGetProperty("error_code", out _) && Number(response, "error_code") != 0)
                throw new InvalidDataException("cloud-baidu-upload-part-rejected");
            if (!string.Equals(Read(response, "md5"), hashes[index], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("cloud-baidu-upload-hash");
        }

        fields.Remove("autoinit");
        fields["uploadid"] = uploadId;
        await BaiduFormAsync("create", fields, ct);
    }

    private async Task<bool> HasFileAsync(string path, CancellationToken ct)
    {
        var separator = path.LastIndexOf('/');
        var parent = path[..(separator + 1)];
        var name = path[(separator + 1)..];
        string? cursor = null;
        do
        {
            var page = await ListAsync(parent, cursor, ct);
            if (page.Entries.FirstOrDefault(entry => entry.Name == name) is { } entry) return !entry.IsDirectory;
            cursor = page.NextCursor;
        } while (cursor is not null);
        return false;
    }

    private async Task<JsonElement> BaiduFormAsync(string method, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(fields);
        return await BaiduPostAsync(BaiduUri("file", new() { ["method"] = method }), form, ct);
    }

    private async Task<JsonElement> BaiduPostAsync(Uri uri, HttpContent body, CancellationToken ct)
    {
        var result = await PostJsonAsync(uri, body, ct);
        if (Number(result, "errno") is { } code && code == 0) return result;
        if (Number(result, "errno") == -8)
            throw new HttpRequestException("cloud-storage-conflict", null, HttpStatusCode.Conflict);
        throw new InvalidDataException("cloud-baidu-write");
    }

    private async Task<JsonElement> PostJsonAsync(Uri uri, HttpContent body, CancellationToken ct)
    {
        using var client = transport(uri, false);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = body };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await JsonAsync(response, ct);
    }
}
