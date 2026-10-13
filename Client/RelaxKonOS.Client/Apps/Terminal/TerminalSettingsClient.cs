using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

public sealed class TerminalSettingsClient : ITerminalSettingsClient
{
    private readonly HttpClient _http;

    private readonly RelaxKonOS.Client.Services.PresentationSettingsChanges _changes;
    public TerminalSettingsClient(HttpClient http, RelaxKonOS.Client.Services.PresentationSettingsChanges changes) { _http = http; _changes = changes; }

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
            Content = content is null ? null : JsonContent.Create(content, options: RelaxKonOSJsonOptions.Default),
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var saved = await response.Content.ReadFromJsonAsync<TerminalSettingsDto>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("Server returned no terminal settings.");
        if (method == HttpMethod.Put) _changes.Publish(new Uri(serverUrl).GetLeftPart(UriPartial.Authority), workspaceId, saved);
        return saved;
    }
}
