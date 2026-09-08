using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

public sealed class TerminalSettingsClient : ITerminalSettingsClient
{
    private readonly HttpClient _http;

    public TerminalSettingsClient(HttpClient http) => _http = http;

    public Task<TerminalSettingsDto> GetAsync(
        string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, serverUrl, accessToken, workspaceId, null, ct);

    public Task<TerminalSettingsDto> SaveAsync(
        string serverUrl, string accessToken, Guid workspaceId, TerminalSettingsDto settings, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, serverUrl, accessToken, workspaceId, settings, ct);

    private async Task<TerminalSettingsDto> SendAsync(
        HttpMethod method, string serverUrl, string accessToken, Guid workspaceId,
        TerminalSettingsDto? content, CancellationToken ct)
    {
        var route = WorkspaceApiRoutes.TerminalSettings.Replace("{id}", workspaceId.ToString());
        using var request = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            Content = content is null ? null : JsonContent.Create(content, options: RemoteOsJsonOptions.Default),
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TerminalSettingsDto>(RemoteOsJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("Server returned no terminal settings.");
    }
}
