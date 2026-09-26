using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.EventAlerts;

namespace RelaxKonOS.Client.Apps.EventAlerts;

public interface IRemoteEventAlertClient
{
    Task<EventAlertSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);
    Task<EventAlertPageDto<OperationalAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only first desktop surface; mutating controls remain explicitly permission-gated.</summary>
public sealed class RemoteEventAlertClient(HttpClient http, IAuthSession session) : IRemoteEventAlertClient
{
    public Task<EventAlertSummaryDto> SummaryAsync(CancellationToken cancellationToken = default) => GetAsync<EventAlertSummaryDto>(EventAlertApiRoutes.Summary, cancellationToken);
    public Task<EventAlertPageDto<OperationalAlertDto>> ListAlertsAsync(CancellationToken cancellationToken = default) => GetAsync<EventAlertPageDto<OperationalAlertDto>>(EventAlertApiRoutes.Alerts, cancellationToken);

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.EffectiveBaseUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(session.EffectiveBaseUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The Event & Alert Center returned an empty response.");
    }
}
