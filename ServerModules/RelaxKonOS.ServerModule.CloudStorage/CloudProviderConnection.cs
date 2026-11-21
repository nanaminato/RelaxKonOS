using System.Net;
using System.Net.Http.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed class CloudProviderConnection(CloudConnectionRequest connection, ICloudFileProviders provider)
{
    internal static CloudFileProvider? Kind(string protocol) => protocol switch
    {
        "webdav" => CloudFileProvider.WebDav,
        "s3" => CloudFileProvider.S3,
        _ => null,
    };

    internal async Task<CloudDirectoryDto> ListAsync(string path, string? cursor, CancellationToken ct)
    {
        using var response = await SendAsync(CloudProviderAction.List, path, ct, cursor: cursor);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudDirectoryDto>(cancellationToken: ct)
            ?? throw new InvalidDataException("cloud-storage-provider-response");
    }

    internal async Task<CloudEntryDto?> InfoAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(CloudProviderAction.Info, path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudEntryDto>(cancellationToken: ct)
            ?? throw new InvalidDataException("cloud-storage-provider-response");
    }

    internal ValueTask<HttpResponseMessage> DownloadAsync(string path, CancellationToken ct) =>
        SendAsync(CloudProviderAction.Download, path, ct);

    internal Task CreateFolderAsync(string path, CancellationToken ct) =>
        CompleteAsync(CloudProviderAction.CreateFolder, path, ct);

    internal Task MoveAsync(string path, string destination, CancellationToken ct) =>
        CompleteAsync(CloudProviderAction.Move, path, ct, destination);

    internal Task DeleteAsync(string path, CancellationToken ct) =>
        CompleteAsync(CloudProviderAction.Delete, path, ct);

    internal Task UploadAsync(string path, Stream stream, CancellationToken ct, bool replace) =>
        CompleteAsync(CloudProviderAction.Upload, path, ct, content: stream, replace: replace);

    private async Task CompleteAsync(CloudProviderAction action, string path, CancellationToken ct, string? destination = null,
        Stream? content = null, bool replace = false)
    {
        using var response = await SendAsync(action, path, ct, destination: destination, content: content, replace: replace);
        response.EnsureSuccessStatusCode();
    }

    private ValueTask<HttpResponseMessage> SendAsync(CloudProviderAction action, string path, CancellationToken ct,
        string? cursor = null, string? destination = null, Stream? content = null, bool replace = false) =>
        provider.SendAsync(Kind(connection.Protocol) ?? throw new ArgumentException("protocol"),
            new(action, connection.Endpoint, connection.UserName, connection.Secret, connection.AllowPrivateWebDav,
                path, cursor, destination, replace, content is null ? null : content.Length - content.Position,
                connection.Region, connection.Bucket, connection.Prefix, connection.SessionToken), content, ct);
}
