using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.Client.Apps.Databases;

public interface IPostgreSqlClient { Task<PostgreSqlState> ReadAsync(CancellationToken ct); Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken ct); Task<PostgreSqlState> MigrationPlanAsync(CancellationToken ct); Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken ct); }
public sealed class PostgreSqlClient(HttpClient http, IAuthSession session) : IPostgreSqlClient
{
    private readonly object? login = session.CurrentSession;
    public Task<PostgreSqlState> ReadAsync(CancellationToken ct) => ReadStateAsync(PostgreSqlRoutes.State,ct);
    public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken ct) => ReadStateAsync(PostgreSqlRoutes.MaintenancePlan,ct);
    public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken ct) => ReadStateAsync(PostgreSqlRoutes.MigrationPlan,ct);
    private async Task<PostgreSqlState> ReadStateAsync(string route,CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(session.EffectiveBaseUrl!), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "database.state_unavailable";
            try
            {
                using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (problem.RootElement.TryGetProperty("code", out var value) && value.GetString() is { } reported
                    && reported.StartsWith("database.", StringComparison.Ordinal)) code = reported;
            }
            catch (System.Text.Json.JsonException) { }
            throw new RelaxKonOS.Client.Services.Installation.InstallationApiException(code, response.StatusCode);
        }
        var state = await response.Content.ReadFromJsonAsync<PostgreSqlState>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidDataException("database.state_unavailable");
        Verify(); return state;
    }
    public async Task StageCredentialAsync(PostgreSqlCredentialRequest value, CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct); Verify();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(session.EffectiveBaseUrl!), PostgreSqlRoutes.Credentials.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, ct); Verify();
        if (!response.IsSuccessStatusCode)
        {
            var code = "database.credential_unavailable";
            try { using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("title", out var title) && title.GetString() == "elevation-required") code = "elevation-required"; }
            catch (System.Text.Json.JsonException) { }
            throw new RelaxKonOS.Client.Services.Installation.InstallationApiException(code, response.StatusCode);
        }
        var reference = await response.Content.ReadFromJsonAsync<PostgreSqlCredentialReference>(RelaxKonOSJsonOptions.Default, ct);
        if (reference?.Reference != value.Reference) throw new InvalidDataException("database.credential_unavailable");
    }
    private void Verify()
    {
        if (login is null || !ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("database.session_changed");
    }
}
