using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>
/// HTTP implementation of <see cref="IRemoteWebServerClient"/>. Mutating endpoints require an
/// Idempotency-Key header (server-enforced); each call generates a fresh key so retries never
/// duplicate an operation. 202 Accepted responses carry the operation dto in the body.
/// </summary>
public sealed class RemoteWebServerClient(HttpClient http, IAuthSession session) : IRemoteWebServerClient
{
    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<WebServerDto>>(HttpMethod.Post, WebServerApiRoutes.Discover, null, null, cancellationToken);

    public Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<WebServerDto>>(HttpMethod.Get, WebServerApiRoutes.WebServers, null, null, cancellationToken);

    public Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerStatusDto?>(HttpMethod.Get, WebServerApiRoutes.Status.Replace("{id}", WebUtility.UrlEncode(id)), null, null, cancellationToken);

    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerConfigTestResultDto?>(HttpMethod.Post, WebServerApiRoutes.TestConfiguration.Replace("{id}", WebUtility.UrlEncode(id)), null, null, cancellationToken);

    public Task<WebServerOperationDto?> IntegrateAsync(string id, IntegrateWebServerRequest request, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.Integrate.Replace("{id}", WebUtility.UrlEncode(id)), request, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.Lifecycle.Replace("{id}", WebUtility.UrlEncode(id)).Replace("{action}", action.ToString().ToLowerInvariant()), null, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.Reload.Replace("{id}", WebUtility.UrlEncode(id)), null, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Get, WebServerApiRoutes.Operations.Replace("{operationId}", operationId.ToString("N")), null, null, cancellationToken);

    public Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.CancelOperation.Replace("{operationId}", operationId.ToString("N")), null, NewKey(), cancellationToken);

    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<WebServerSiteDto>?>(HttpMethod.Get, WebServerApiRoutes.Sites.Replace("{id}", WebUtility.UrlEncode(id)), null, null, cancellationToken);

    public async Task<WebServerSiteDto?> UpsertSiteAsync(string id, UpsertWebServerSiteRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync<WebServerSiteDto?>(HttpMethod.Post, WebServerApiRoutes.Sites.Replace("{id}", WebUtility.UrlEncode(id)), request, NewKey(), cancellationToken);
        }
        catch (WebServerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict && exception.ProblemCode == "webserver.http_409")
        {
            // A 409 is the API's conflict status for site creation. Keep the user-facing error
            // useful even if an intermediary stripped the server's JSON problemCode body.
            throw new WebServerApiException("webserver.site_conflict", exception.StatusCode??HttpStatusCode.NoContent);
        }
    }

    public async Task DeleteSiteAsync(string id, string siteId, CancellationToken cancellationToken = default)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(new Uri(session.ServerUrl), WebServerApiRoutes.SiteById.Replace("{id}", WebUtility.UrlEncode(id)).Replace("{siteId}", WebUtility.UrlEncode(siteId)).TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Unable to delete site ({(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && default(T) is null)
            return default!;
        if (!response.IsSuccessStatusCode)
        {
            throw new WebServerApiException(await ReadProblemCodeAsync(response, cancellationToken)
                ?? FallbackProblemCode(response.StatusCode), response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }

    /// <summary>
    /// Error responses normally carry a problemCode JSON object. A proxy, an older server, or a
    /// connection aborted after the status line can still yield an empty/non-JSON error body; that
    /// must not hide the original HTTP failure behind a JSON parsing exception.
    /// </summary>
    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<WebServerProblemDetails>(payload, RelaxKonOSJsonOptions.Default)?.ProblemCode;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FallbackProblemCode(HttpStatusCode statusCode)
        => $"webserver.http_{(int)statusCode}";

    private sealed record WebServerProblemDetails(string? ProblemCode);
}

/// <summary>Structured server failure exposed to the view model as a stable problem code.</summary>
internal sealed class WebServerApiException(string problemCode, HttpStatusCode statusCode)
    : HttpRequestException(problemCode, null, statusCode)
{
    public string ProblemCode { get; } = problemCode;
}
