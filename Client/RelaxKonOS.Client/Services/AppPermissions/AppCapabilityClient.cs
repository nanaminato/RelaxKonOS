using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Capabilities;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>Host-only client for issuing app-scoped file credentials and maintaining media leases.</summary>
public interface IAppCapabilityClient
{
    Task<FileCapabilityTokenDto> IssueFileTokenAsync(string appId, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default);
    Task<MediaLeaseDto> CreateMediaLeaseAsync(string appId, string path, CancellationToken cancellationToken = default);
    Task<MediaLeaseDto> RenewMediaLeaseAsync(string leaseId, CancellationToken cancellationToken = default);
    Task RevokeMediaLeaseAsync(string leaseId, CancellationToken cancellationToken = default);
}

public sealed class AppCapabilityClient(HttpClient http, IAuthSession session) : IAppCapabilityClient
{
    public Task<FileCapabilityTokenDto> IssueFileTokenAsync(string appId, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
        => SendAsync<FileCapabilityTokenDto>(HttpMethod.Post, AppCapabilityRoutes.FileToken,
            new IssueFileCapabilityRequest(appId, scopes), cancellationToken);

    public Task<MediaLeaseDto> CreateMediaLeaseAsync(string appId, string path, CancellationToken cancellationToken = default)
        => SendAsync<MediaLeaseDto>(HttpMethod.Post, AppCapabilityRoutes.MediaLeases,
            new CreateMediaLeaseRequest(appId, path), cancellationToken);

    public Task<MediaLeaseDto> RenewMediaLeaseAsync(string leaseId, CancellationToken cancellationToken = default)
        => SendAsync<MediaLeaseDto>(HttpMethod.Post, AppCapabilityRoutes.MediaLease(leaseId) + "/renew", null, cancellationToken);

    public async Task RevokeMediaLeaseAsync(string leaseId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, AppCapabilityRoutes.MediaLease(leaseId), cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(method, route, cancellationToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>(RelaxKonOSJsonOptions.Default, cancellationToken);
            var message = problem?.Detail ?? problem?.Title
                ?? $"The capability request failed with HTTP {(int)response.StatusCode}.";
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty capability response.");
    }

    private Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string route, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Sign in before requesting application capabilities.");

        var uri = new Uri(new Uri(session.ServerUrl, UriKind.Absolute), route.TrimStart('/'));
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return Task.FromResult(request);
    }

    private sealed record ProblemDetailsDto(string? Title, string? Detail);
}
