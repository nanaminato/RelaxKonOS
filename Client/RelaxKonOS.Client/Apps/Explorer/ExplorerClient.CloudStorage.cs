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

    internal async Task DownloadCloudEntryAsync(Guid id, string reference, Stream output, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, CloudEntryRoute(id, "download", reference, null),
            ct: ct, completion: HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, ct);
        await response.Content.CopyToAsync(output, ct);
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
