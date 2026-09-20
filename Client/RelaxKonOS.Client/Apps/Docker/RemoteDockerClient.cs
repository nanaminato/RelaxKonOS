using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Typed JWT client for the server's local Docker facade.</summary>
public sealed class RemoteDockerClient(HttpClient http, IAuthSession session) : IRemoteDockerClient
{
    public Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync<DockerStatusDto>(DockerApiRoutes.Status, cancellationToken);
    public Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerContainerDto>>(DockerApiRoutes.Containers, cancellationToken);
    public Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerImageDto>>(DockerApiRoutes.Images, cancellationToken);
    public Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerNetworkDto>>(DockerApiRoutes.Networks, cancellationToken);
    public Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerVolumeDto>>(DockerApiRoutes.Volumes, cancellationToken);
    public async Task<DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken cancellationToken = default) => await TrySendAsync<DockerContainerDetailsDto>(DockerApiRoutes.ContainerById.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);
    public Task<IReadOnlyList<DockerStackDto>> ListStacksAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerStackDto>>(DockerApiRoutes.Stacks, cancellationToken);
    public Task<IReadOnlyList<DockerStackServiceDto>> ListStackServicesAsync(string name, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<DockerStackServiceDto>>(DockerApiRoutes.StackServices.Replace("{name}", Uri.EscapeDataString(name)), cancellationToken);
    public Task<DockerOperationResult> ApplyContainerActionAsync(string id, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Post, DockerApiRoutes.ContainerAction.Replace("{id}", Uri.EscapeDataString(id)).Replace("{action}", action), request, cancellationToken);
    public Task<DockerStackOperationResult> ApplyStackOperationAsync(string operation, DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        var route = operation switch { "validate" => DockerApiRoutes.StackValidate, "deploy" => DockerApiRoutes.StackDeploy, _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
        return SendAsync<DockerStackOperationResult>(HttpMethod.Post, route, definition, cancellationToken);
    }
    public Task<DockerStackOperationResult> ApplyStackActionAsync(string name, string action, DockerStackActionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<DockerStackOperationResult>(HttpMethod.Post, DockerApiRoutes.StackAction.Replace("{name}", Uri.EscapeDataString(name)).Replace("{action}", action), request, cancellationToken);
    public Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Post, DockerApiRoutes.ImagePull, request, cancellationToken);
    public Task<DockerOperationResult> DeleteImageAsync(string id, DockerImageOperationRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Delete, DockerApiRoutes.ImageDelete.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);
    public Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Post, DockerApiRoutes.Containers, request, cancellationToken);
    public Task<DockerOperationResult> UpdateContainerAsync(string id, DockerContainerUpdateRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Put, DockerApiRoutes.ContainerById.Replace("{id}", Uri.EscapeDataString(id)), request, cancellationToken);
    public async Task<DockerStackDefinitionDto?> GetStackDefinitionAsync(string name, CancellationToken cancellationToken = default) => await TrySendAsync<DockerStackDefinitionDto>(DockerApiRoutes.StackDefinition.Replace("{name}", Uri.EscapeDataString(name)), cancellationToken);
    public Task<DockerOperationResult> CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Post, DockerApiRoutes.Networks, request, cancellationToken);
    public Task<DockerOperationResult> CreateVolumeAsync(DockerVolumeCreateRequest request, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Post, DockerApiRoutes.Volumes, request, cancellationToken);
    public async Task<DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken cancellationToken = default) => await TrySendAsync<DockerNetworkDetailsDto>(DockerApiRoutes.NetworkById.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);
    public async Task<DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken cancellationToken = default) => await TrySendAsync<DockerVolumeDetailsDto>(DockerApiRoutes.VolumeByName.Replace("{name}", Uri.EscapeDataString(name)), cancellationToken);
    public Task<DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Delete, $"{DockerApiRoutes.NetworkById.Replace("{id}", Uri.EscapeDataString(id))}?confirmed={confirmed.ToString().ToLowerInvariant()}", null, cancellationToken);
    public Task<DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken cancellationToken = default) => SendAsync<DockerOperationResult>(HttpMethod.Delete, $"{DockerApiRoutes.VolumeByName.Replace("{name}", Uri.EscapeDataString(name))}?confirmed={confirmed.ToString().ToLowerInvariant()}", null, cancellationToken);
    public async Task<DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail = 200, CancellationToken cancellationToken = default) => await TrySendAsync<DockerContainerLogsDto>($"{DockerApiRoutes.ContainerLogs.Replace("{id}", Uri.EscapeDataString(id))}?tail={tail}", cancellationToken);
    public async Task<DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken cancellationToken = default) => await TrySendAsync<DockerContainerStatsDto>(DockerApiRoutes.ContainerStats.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);
    public Task<DockerEngineControlResult> ApplyEngineActionAsync(DockerEngineAction action, bool confirmed, CancellationToken cancellationToken = default) =>
        SendAsync<DockerEngineControlResult>(HttpMethod.Post,
            DockerApiRoutes.EngineAction.Replace("{action}", DockerEngineActionRoutes.Segment(action)),
            new DockerEngineActionRequest(confirmed), cancellationToken);
    public Task<DockerProxyStatusDto> GetProxyStatusAsync(CancellationToken cancellationToken = default) => SendAsync<DockerProxyStatusDto>(DockerProxyApiRoutes.Proxy, cancellationToken);
    public Task<DockerProxyStatusDto> SaveProxyAsync(SaveDockerProxySettingsRequest request, CancellationToken cancellationToken = default) => SendProxyAsync(HttpMethod.Put, request, cancellationToken);
    public Task<DockerProxyStatusDto> ClearProxyAsync(CancellationToken cancellationToken = default) => SendProxyAsync(HttpMethod.Delete, null, cancellationToken);

    /// <summary>
    /// A proxy write answers with the full status on success and with a problem document on
    /// rejection. The status also has to survive a refusal, so the response is inspected before
    /// <c>EnsureSuccessStatusCode</c> would collapse it into a generic transport error.
    /// </summary>
    private async Task<DockerProxyStatusDto> SendProxyAsync(HttpMethod method, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("docker.error.not_signed_in"));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), DockerProxyApiRoutes.Proxy.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DockerProxyRequestException(await ReadProblemCodeAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<DockerProxyStatusDto>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("docker.error.empty_response"));
    }

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const string fallback = "docker.proxy.problem.request_failed";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            // The server sends the code both as the problem title and as an extension member, so
            // either is accepted; a body that is not a problem document carries no code at all.
            if (root.TryGetProperty("problemCode", out var code) && code.GetString() is { Length: > 0 } extension)
                return extension;
            if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } fromTitle && fromTitle.StartsWith("docker.", StringComparison.Ordinal))
                return fromTitle;
        }
        catch (JsonException) { /* A non-JSON body cannot carry a problem code. */ }
        return fallback;
    }

    private Task<T> SendAsync<T>(string route, CancellationToken cancellationToken) => SendAsync<T>(HttpMethod.Get, route, null, cancellationToken);
    private async Task<T?> TrySendAsync<T>(string route, CancellationToken cancellationToken) where T : class
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("docker.error.not_signed_in"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("docker.error.not_signed_in"));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("docker.error.empty_response"));
    }
}
