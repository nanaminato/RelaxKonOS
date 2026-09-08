using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.Certificates;

/// <summary>
/// HTTP implementation of <see cref="IRemoteCertificateClient"/>. Mutating endpoints require an
/// Idempotency-Key header (server-enforced); each call generates a fresh key so retries never
/// duplicate an operation. 202 Accepted responses carry the operation dto in the body.
/// </summary>
public sealed class RemoteCertificateClient(HttpClient http, IAuthSession session) : IRemoteCertificateClient
{
    public Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<CertificateDto>>(HttpMethod.Get, CertificateApiRoutes.Certificates, null, null, cancellationToken);

    public Task<CertificateDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateDto?>(HttpMethod.Get, CertificateApiRoutes.ById.Replace("{id}", id.ToString("N")), null, null, cancellationToken, returnNullOnNotFound: true);

    public Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificatePreflightResultDto>(HttpMethod.Post, CertificateApiRoutes.Preflight, request, null, cancellationToken);

    public Task<CertificateOperationDto> RequestAsync(RequestCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.Request, request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> CreateSelfSignedAsync(CreateSelfSignedCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.SelfSigned, request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> DeployKestrelAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.Deploy.Replace("{id}", id.ToString("N")), null, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> RenewAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.Renew.Replace("{id}", id.ToString("N")), null, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> RevokeAsync(Guid id, RevokeCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.Revoke.Replace("{id}", id.ToString("N")), request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> DeleteAsync(Guid id, DeleteCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Delete, CertificateApiRoutes.ById.Replace("{id}", id.ToString("N")), request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto?>(HttpMethod.Get, CertificateApiRoutes.Operations.Replace("{operationId}", operationId.ToString("N")), null, null, cancellationToken, returnNullOnNotFound: true);

    public Task<CertificateOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto?>(HttpMethod.Post, CertificateApiRoutes.CancelOperation.Replace("{operationId}", operationId.ToString("N")), null, null, cancellationToken, returnNullOnNotFound: true);

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? idempotencyKey, CancellationToken cancellationToken,
        bool returnNullOnNotFound = false)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken);
        // Only lookup endpoints opt into a null result. Treating a collection or mutation 404 as
        // null hides a bad API route and leads to secondary null-reference failures in the UI.
        if (response.StatusCode == HttpStatusCode.NotFound && returnNullOnNotFound)
            return default!;
        if (!response.IsSuccessStatusCode)
        {
            throw new CertificateApiException(await ReadProblemCodeAsync(response, cancellationToken)
                ?? FallbackProblemCode(response.StatusCode), response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize<CertificateProblemDetails>(payload, RemoteOsJsonOptions.Default)?.ProblemCode; }
        catch (JsonException) { return null; }
    }

    private static string FallbackProblemCode(HttpStatusCode statusCode) => $"certificate.http_{(int)statusCode}";

    private sealed record CertificateProblemDetails(string? ProblemCode);
}

/// <summary>Structured certificate API failure exposed to the view model as a stable problem code.</summary>
internal sealed class CertificateApiException(string problemCode, HttpStatusCode statusCode)
    : HttpRequestException(problemCode, null, statusCode)
{
    public string ProblemCode { get; } = problemCode;
}
