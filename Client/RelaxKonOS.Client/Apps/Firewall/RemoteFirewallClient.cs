using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Client.Services.Privileged;

namespace RelaxKonOS.Client.Apps.Firewall;

public sealed class RemoteFirewallClient(HttpClient http, IAuthSession session, IHostElevationBroker elevation) : IRemoteFirewallClient
{
    public Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync<FirewallStatusDto>(HttpMethod.Get, FirewallApiRoutes.Status, null, cancellationToken);
    public Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<FirewallRuleDto>>(HttpMethod.Get, FirewallApiRoutes.Rules, null, cancellationToken);
    public Task<FirewallOperationResult> SetEnabledAsync(UpdateFirewallEnabledRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Enabled, request, cancellationToken);
    public Task<FirewallOperationResult> SetDefaultsAsync(UpdateFirewallDefaultsRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Defaults, request, cancellationToken);
    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Post, FirewallApiRoutes.Rules, request, cancellationToken);
    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Put, FirewallApiRoutes.Rule.Replace("{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), request, cancellationToken);
    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken = default) => SendAsync<FirewallOperationResult>(HttpMethod.Delete, FirewallApiRoutes.Rule.Replace("{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
        => method == HttpMethod.Get ? await SendOnceAsync<T>(method, route, body, cancellationToken)
            : await elevation.ExecuteAsync(HostElevationCapability.FirewallChange, "host/firewall",
                () => SendOnceAsync<T>(method, route, body, cancellationToken), cancellationToken);

    private async Task<T> SendOnceAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.EffectiveBaseUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
        if (result is FirewallOperationResult { Success: false, ProblemCode: "firewall.elevation_required" })
            throw new RelaxKonOSAuthException(new ProblemDetails("https://relaxkonos.app/problems/elevation-required", "Elevation", 403, null, null));
        return result;
    }
}
