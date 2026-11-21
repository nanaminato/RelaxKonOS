using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.Client.Services.Databases;

public sealed class MySqlClient(HttpClient http, IAuthSession session) : IServerMySql
{
    private readonly object? _login = session.CurrentSession;

    public async Task<MySqlState?> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, MySqlRoutes.State, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MySqlState>(RelaxKonOSJsonOptions.Default, cancellationToken);
    }

    public async Task<HttpStatusCode> StageCredentialAsync(PostgreSqlCredentialRequest value, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, MySqlRoutes.Credentials, cancellationToken);
        request.Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string route, CancellationToken cancellationToken)
    {
        var address = session.EffectiveBaseUrl;
        VerifyConnection(address);
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: cancellationToken);
        VerifyConnection(address);
        return new HttpRequestMessage(method, new Uri(new Uri(address!), route.TrimStart('/')))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
    }

    private void VerifyConnection(string? address)
    {
        if (_login is null || !ReferenceEquals(_login, session.CurrentSession)
            || session.State != AuthSessionState.Authenticated || address is null || address != session.EffectiveBaseUrl)
            throw new InvalidOperationException("database.session_changed");
    }
}
