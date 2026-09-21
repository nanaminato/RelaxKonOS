using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Foundation.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Foundation.Http;

/// <summary>Authentication endpoints are the first extracted typed client. Callers supply a server URL per request, avoiding mutable shared BaseAddress state.</summary>
public sealed class RelaxKonOSClient(HttpClient http) : IRelaxKonOSClient
{
    public async Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(BuildUri(serverUrl, AuthApiRoutes.Login), request, RelaxKonOSJsonOptions.Default, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The server returned an empty login response.");
    }

    public async Task LogoutAsync(string serverUrl, string accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(serverUrl, AuthApiRoutes.Logout))
        {
            Content = JsonContent.Create(new LogoutRequest(refreshToken), options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildUri(string serverUrl, string route)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new UriFormatException("Server URL must be an absolute HTTP or HTTPS URL.");
        return new Uri(baseUri, route.TrimStart('/'));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(problem?.Detail ?? $"Server returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
