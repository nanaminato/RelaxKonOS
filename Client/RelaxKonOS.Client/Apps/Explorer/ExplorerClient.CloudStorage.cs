using System.Net;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed partial class ExplorerClient
{
    internal async Task<IReadOnlyList<CloudConnectionDto>?> CloudConnectionsAsync(CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, CloudStorageApi.Route + "/connections", ct: ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
        return await ReadAsync<CloudConnectionDto[]>(response, ct);
    }

    internal Task<CloudConnectionDto> SaveCloudConnectionAsync(Guid? id, CloudConnectionRequest request, CancellationToken ct) =>
        SendAsync<CloudConnectionDto>(id is null ? HttpMethod.Post : HttpMethod.Put,
            id is null ? CloudStorageApi.Route + "/connections" : CloudRoute(id.Value), body: request, ct: ct);

    internal async Task RevokeCloudConnectionAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, CloudRoute(id), ct: ct);
        await EnsureSuccessAsync(response, ct);
    }

    internal Task<CloudDirectoryDto> CloudEntriesAsync(Guid id, string? reference, string? cursor, CancellationToken ct) =>
        SendAsync<CloudDirectoryDto>(HttpMethod.Get, CloudEntryRoute(id, "entries", reference, cursor), ct: ct);

    /// <summary>
    /// Streams one cloud object into <paramref name="output"/>. The transfer reports confirmed
    /// bytes as they land, because a provider may answer without a length and a download that
    /// cannot state its progress should say so instead of freezing a bar at zero.
    /// </summary>
    internal async Task DownloadCloudEntryAsync(Guid id, string reference, Stream output,
        Action<long, long?> progress, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, CloudEntryRoute(id, "download", reference, null),
            ct: ct, completion: HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, ct);
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long copied = 0;
        progress(copied, total);
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            progress(copied, total);
        }
    }

    internal async Task CreateCloudFolderAsync(Guid id, string? reference, string name, CancellationToken ct)
    {
        var route = CloudEntryRoute(id, "folders", reference, null) + "&name=" + Uri.EscapeDataString(name);
        using var response = await SendRawAsync(HttpMethod.Post, route, ct: ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static string CloudRoute(Guid id) => CloudStorageApi.Route + "/connections/" + id.ToString("D");

    private static string CloudEntryRoute(Guid id, string action, string? reference, string? cursor) =>
        CloudRoute(id) + "/" + action + "?reference=" + Uri.EscapeDataString(reference ?? "")
        + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
}
