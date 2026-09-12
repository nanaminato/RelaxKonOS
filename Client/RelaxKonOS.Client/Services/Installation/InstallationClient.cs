using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Services.Installation;

public sealed class InstallationApiException(string code, HttpStatusCode status) : Exception(code)
{
    public string ProblemCode { get; } = code;
    public HttpStatusCode Status { get; } = status;
}

public sealed class InstallationClient(HttpClient http, IAuthSession session)
{
    public Task<InstallationOperationDto?> StartAsync(InstallationServiceId service, InstallationOperationKind kind, object options, string key, CancellationToken ct)
        => SendAsync<InstallationOperationDto>(HttpMethod.Post, InstallationApiRoutes.Start(service, kind), options, key, ct);
    public Task<InstallationOperationDto?> GetAsync(Guid id, CancellationToken ct) => SendAsync<InstallationOperationDto>(HttpMethod.Get, InstallationApiRoutes.Operation(id), null, null, ct);
    public Task<InstallationOperationDto?> GetActiveAsync(InstallationServiceId service, CancellationToken ct) => SendAsync<InstallationOperationDto>(HttpMethod.Get, InstallationApiRoutes.Active(service), null, null, ct);
    public Task<InstallationFileReferenceDto?> CreateFileReferenceAsync(InstallationServiceId service, string path, CancellationToken ct) =>
        SendAsync<InstallationFileReferenceDto>(HttpMethod.Post, InstallationApiRoutes.FileReference(service), new CreateInstallationFileReferenceRequest(path), Guid.NewGuid().ToString("N"), ct);
    public Task<InstallationOperationDto?> CancelAsync(Guid id, CancellationToken ct) => SendAsync<InstallationOperationDto>(HttpMethod.Post, InstallationApiRoutes.Cancel(id), null, Guid.NewGuid().ToString("N"), ct);
    public async Task<bool> ElevateAsync(InstallationServiceId service, string password, CancellationToken ct)
    {
        var capability = service switch
        {
            InstallationServiceId.Smb => HostElevationCapability.SmbInstall, InstallationServiceId.Nginx => HostElevationCapability.NginxInstall,
            InstallationServiceId.Frp => HostElevationCapability.FrpInstall, InstallationServiceId.Mihomo => HostElevationCapability.MihomoInstall,
            InstallationServiceId.Git => HostElevationCapability.GitPackageInstall, _ => HostElevationCapability.DockerInstall
        };
        var result = await SendAsync<HostElevationResult>(HttpMethod.Post, PrivilegedApiRoutes.Elevation,
            new HostElevationRequest(capability, service.ToString().ToLowerInvariant(), password), null, ct);
        return result?.Elevated == true;
    }
    private async Task<T?> SendAsync<T>(HttpMethod method, string route, object? body, string? key, CancellationToken ct)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InstallationApiException("installation.signed_out", HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), cancellationToken: ct));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
        {
            var code = "installation.connection_unavailable";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("problemCode", out var problem)) code = problem.GetString() ?? code;
            }
            catch (JsonException) { }
            throw new InstallationApiException(code, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct);
    }
}
