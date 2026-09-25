using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Captured stable identity of the login an account-security write belongs to.
/// <paramref name="ServiceId"/> — not the current transport address — decides whether the login is
/// still the same one, so a verified tunnel rebind does not cancel an in-flight write.</summary>
public sealed record AccountSecurityConnection(string ServiceId, Guid UserId, Guid SessionId);

/// <summary>Passwords are sent once. Refresh completes before creating the sensitive request.</summary>
public sealed class AccountSecurityClient(HttpClient http, IAuthSession session)
{
    public AccountSecurityConnection Capture() => session is { State: AuthSessionState.Authenticated, ServiceId: { } serviceId,
        CurrentUser: { } user, CurrentSession: { } current } ? new(serviceId, user.Id, current.Id) : throw new InvalidOperationException("Not connected.");
    public bool IsCurrent(AccountSecurityConnection connection) => session.State == AuthSessionState.Authenticated
        && session.ServiceId == connection.ServiceId && session.CurrentUser?.Id == connection.UserId && session.CurrentSession?.Id == connection.SessionId;

    public async Task<AliasConfigurationDto> ReadAsync(AccountSecurityConnection connection, CancellationToken ct)
        => (await SendAsync(connection, HttpMethod.Get, AuthApiRoutes.LoginAlias, null, ct))!;

    public Task<AliasConfigurationDto?> ChangeAsync(AccountSecurityConnection connection, object request, CancellationToken ct)
    {
        var (method, route) = request switch
        {
            CreateAliasRequest => (HttpMethod.Post, AuthApiRoutes.LoginAlias),
            RenameAliasRequest => (HttpMethod.Put, AuthApiRoutes.LoginAlias),
            ChangeAliasPasswordRequest => (HttpMethod.Put, AuthApiRoutes.AliasPassword),
            DeleteAliasRequest => (HttpMethod.Post, AuthApiRoutes.DeleteAlias),
            SetSystemLoginRequest => (HttpMethod.Put, AuthApiRoutes.SystemLogin),
            _ => throw new ArgumentException("Unknown account operation.")
        };
        return SendAsync(connection, method, route, request, ct);
    }

    private async Task<AliasConfigurationDto?> SendAsync(AccountSecurityConnection connection, HttpMethod method, string route, object? payload, CancellationToken ct)
    {
        var baseUrl = CurrentBaseUrl(connection);
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        if (token is null) throw new OperationCanceledException(ct);
        baseUrl = CurrentBaseUrl(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (payload is not null) request.Content = JsonContent.Create(payload, payload.GetType(), options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!IsCurrent(connection)) throw new OperationCanceledException(ct);
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, timeout.Token); }
            catch (System.Text.Json.JsonException) { }
            throw new RelaxKonOSAuthException(problem ?? new("https://relaxkonos.app/problems/authentication-unavailable", "Account security unavailable", (int)response.StatusCode, null, null));
        }
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null
            : await response.Content.ReadFromJsonAsync<AliasConfigurationDto>(RelaxKonOSJsonOptions.Default, timeout.Token)
                ?? throw new InvalidOperationException("Missing account configuration.");
    }

    /// <summary>Reads the transport address at call time; the identity check is what guards the write.</summary>
    private string CurrentBaseUrl(AccountSecurityConnection connection)
    {
        if (!IsCurrent(connection) || session.EffectiveBaseUrl is not { } url)
            throw new OperationCanceledException();
        return url;
    }
}
