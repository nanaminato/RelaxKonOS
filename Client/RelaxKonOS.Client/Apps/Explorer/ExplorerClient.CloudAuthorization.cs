using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed partial class ExplorerClient
{
    internal Task<CloudAuthorizationProviderDto[]> CloudAuthorizationProvidersAsync(CancellationToken ct) =>
        SendAsync<CloudAuthorizationProviderDto[]>(HttpMethod.Get, CloudStorageApi.Route + "/authorization/providers", ct: ct);

    internal Task<CloudAuthorizationSessionDto> StartCloudAuthorizationAsync(CloudAuthorizationStartRequest request, CancellationToken ct) =>
        SendAsync<CloudAuthorizationSessionDto>(HttpMethod.Post, CloudStorageApi.Route + "/authorization/sessions", body: request, ct: ct);

    internal Task<CloudAuthorizationSessionDto> CloudAuthorizationAsync(Guid id, CancellationToken ct) =>
        SendAsync<CloudAuthorizationSessionDto>(HttpMethod.Get, AuthorizationRoute(id), ct: ct);

    internal async Task CancelCloudAuthorizationAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, AuthorizationRoute(id), ct: ct);
        await EnsureSuccessAsync(response, ct);
    }

    internal async Task ConfigureCloudAuthorizationAsync(string protocol, CloudAuthorizationConfigurationRequest request, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Put, CloudStorageApi.Route + "/authorization/configuration/" + protocol, body: request, ct: ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static string AuthorizationRoute(Guid id) => CloudStorageApi.Route + "/authorization/sessions/" + id.ToString("D");
}
