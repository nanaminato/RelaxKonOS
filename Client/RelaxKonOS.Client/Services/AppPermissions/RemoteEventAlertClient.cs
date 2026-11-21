using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.EventAlerts;

namespace RelaxKonOS.Client.Services.AppPermissions;

public interface IRemoteEventAlertClient
{
    Task<EventAlertSummaryDto> SummaryAsync(UsageMemoryScope connection, CancellationToken cancellationToken);
    Task<EventAlertPageDto<OperationalAlertDto>> ListAlertsAsync(UsageMemoryScope connection, CancellationToken cancellationToken);
}

/// <summary>Read-only first desktop surface; mutating controls remain explicitly permission-gated.</summary>
public sealed class RemoteEventAlertClient(HttpClient http, IAuthSession session) : IRemoteEventAlertClient
{
    public Task<EventAlertSummaryDto> SummaryAsync(UsageMemoryScope connection, CancellationToken cancellationToken) =>
        GetAsync<EventAlertSummaryDto>(connection, EventAlertApiRoutes.Summary, cancellationToken);
    public Task<EventAlertPageDto<OperationalAlertDto>> ListAlertsAsync(UsageMemoryScope connection, CancellationToken cancellationToken) =>
        GetAsync<EventAlertPageDto<OperationalAlertDto>>(connection, EventAlertApiRoutes.Alerts, cancellationToken);

    private async Task<T> GetAsync<T>(UsageMemoryScope connection, string route, CancellationToken cancellationToken)
    {
        var baseUrl = session.EffectiveBaseUrl;
        var tokens = session.Tokens;
        if (!connection.IsCurrent || session.State != AuthSessionState.Authenticated || tokens is null || baseUrl is null)
            throw new InvalidOperationException("The alert connection is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The Event & Alert Center returned an empty response.");
    }
}
