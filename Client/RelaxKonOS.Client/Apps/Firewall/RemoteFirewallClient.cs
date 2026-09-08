using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

public sealed class RemoteFirewallClient(HttpClient http, IAuthSession session) : IRemoteFirewallClient
{
    public Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync<FirewallStatusDto>(HttpMethod.Get, FirewallApiRoutes.Status, null, cancellationToken);
    public Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<FirewallRuleDto>>(HttpMethod.Get, FirewallApiRoutes.Rules, null, cancellationToken);
    public Task<FirewallOperationResult> SetEnabledAsync(UpdateFirewallEnabledRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Enabled, request, cancellationToken);
    public Task<FirewallOperationResult> SetDefaultsAsync(UpdateFirewallDefaultsRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Defaults, request, cancellationToken);
    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Post, FirewallApiRoutes.Rules, request, cancellationToken);
    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Rule.Replace("{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), request, cancellationToken);
    public Task<FirewallOperationResult> DeleteRuleAsync(int number, DeleteFirewallRuleRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Delete, FirewallApiRoutes.Rule.Replace("{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), request, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }
}
