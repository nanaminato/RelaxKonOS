using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.HostSettings;

/// <summary>Shared connection-bound HTTP transport for host settings; never replays writes.</summary>
public abstract class HostSettingsService(HttpClient http, IAuthSession session)
{
    public HostSettingsConnection CaptureConnection()
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is not { } url
            || session.CurrentSession is not { } login || session.CurrentUser is not { } user)
            throw Problem(401, "settings.unauthenticated");
        return new(url, login.Id, user.Id);
    }

    public bool IsCurrent(HostSettingsConnection connection) => session.State == AuthSessionState.Authenticated
        && session.ServerUrl == connection.ServerUrl && session.CurrentSession?.Id == connection.SessionId
        && session.CurrentUser?.Id == connection.UserId;

    protected async Task<T> SendAsync<T>(HostSettingsConnection connection, HttpMethod method, string route, object? body, CancellationToken ct)
    {
        CheckConnection(connection);
        var token = await session.GetAccessTokenAsync(TimeSpan.FromSeconds(30), ct: ct);
        CheckConnection(connection);
        if (string.IsNullOrEmpty(token)) throw Problem(401, "settings.unauthenticated");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(connection.ServerUrl), route))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, ct);
        CheckConnection(connection);
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct); }
            catch (System.Text.Json.JsonException) { }
            throw problem is null ? Problem((int)response.StatusCode, "settings.http_error") : new RelaxKonOSAuthException(problem);
        }
        var result = await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw Problem(502, "settings.empty_response");
        CheckConnection(connection);
        return result;
    }

    private void CheckConnection(HostSettingsConnection connection)
    {
        if (!IsCurrent(connection)) throw Problem(409, "settings.connection_changed");
    }
    private static RelaxKonOSAuthException Problem(int status, string code)
        => new(new ProblemDetails("https://relaxkonos.app/problems/" + code, code, status, null, null));
}
