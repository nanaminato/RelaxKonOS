using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WindowLayout;

public sealed class WindowLayoutClient(HttpClient http) : IWindowLayoutClient
{
    public Task<WorkspaceWindowLayoutDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default)
        => SendAsync<WorkspaceWindowLayoutDto>(HttpMethod.Get, serverUrl, accessToken, workspaceId, null, ct);

    public Task<WorkspaceWindowLayoutDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, WorkspaceWindowLayoutDto layouts, CancellationToken ct = default)
        => SendAsync<WorkspaceWindowLayoutDto>(HttpMethod.Put, serverUrl, accessToken, workspaceId, layouts, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string serverUrl, string accessToken, Guid workspaceId, object? body, CancellationToken ct)
    {
        var route = WorkspaceApiRoutes.WindowLayouts.Replace("{id}", workspaceId.ToString("D"));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The server returned an empty window-layout response.");
    }
}
