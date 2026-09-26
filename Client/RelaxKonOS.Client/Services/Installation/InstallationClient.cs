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

public sealed class InstallationClient(HttpClient http, IAuthSession session, IHttpClientFactory? httpClients = null)
{
    private const long MaximumHostPackageBytes = 128L * 1024 * 1024;
    public Task<InstallationOperationDto?> StartAsync(InstallationServiceId service, InstallationOperationKind kind, object options, string key, CancellationToken ct)
        => SendAsync<InstallationOperationDto>(HttpMethod.Post, InstallationApiRoutes.Start(service, kind), options, key, ct);
    public Task<InstallationOperationDto?> GetAsync(Guid id, CancellationToken ct) => SendAsync<InstallationOperationDto>(HttpMethod.Get, InstallationApiRoutes.Operation(id), null, null, ct);
    public Task<InstallationOperationDto?> GetActiveAsync(InstallationServiceId service, CancellationToken ct) => SendAsync<InstallationOperationDto>(HttpMethod.Get, InstallationApiRoutes.Active(service), null, null, ct);
    public Task<InstallationFileReferenceDto?> CreateFileReferenceAsync(InstallationServiceId service, string path, CancellationToken ct) =>
        SendAsync<InstallationFileReferenceDto>(HttpMethod.Post, InstallationApiRoutes.FileReference(service), new CreateInstallationFileReferenceRequest(path), Guid.NewGuid().ToString("N"), ct);
    public async Task<InstallationFileReferenceDto?> UploadPackageAsync(InstallationServiceId service, string fileName, Stream content, CancellationToken ct)
    {
        if (session.State != AuthSessionState.Authenticated || session.EffectiveBaseUrl is null)
            throw new InstallationApiException("installation.signed_out", HttpStatusCode.Unauthorized);
        using var form = new MultipartFormDataContent();
        using var package = new StreamContent(content);
        form.Add(package, "package", Path.GetFileName(fileName));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(session.EffectiveBaseUrl), InstallationApiRoutes.Package(service).TrimStart('/'))) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ReadExceptionAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<InstallationFileReferenceDto>(RelaxKonOSJsonOptions.Default, ct);
    }
    public async Task<InstallationFileReferenceDto?> DownloadAndUploadPackageAsync(InstallationServiceId service, string url,
        string fileName, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.BadRequest);
        var temporary = Path.Combine(Path.GetTempPath(), "relaxkonos-install-" + Guid.NewGuid().ToString("N") + ".package");
        try
        {
            using var downloader = httpClients?.CreateClient("InstallationHostDownload")
                ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await DownloadHttpsAsync(downloader, source, ct);
            if (!response.IsSuccessStatusCode) throw new InstallationApiException("installation.host_download_failed", response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaximumHostPackageBytes)
                throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.RequestEntityTooLarge);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumHostPackageBytes)
                        throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.RequestEntityTooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await using var staged = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await UploadPackageAsync(service, fileName, staged, ct);
        }
        catch (InstallationApiException) { throw; }
        catch (HttpRequestException) { throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.BadGateway); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
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
        if (session.State != AuthSessionState.Authenticated || session.EffectiveBaseUrl is null) throw new InstallationApiException("installation.signed_out", HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && method == HttpMethod.Get) return default;
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadExceptionAsync(response, ct);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct);
    }
    private static async Task<InstallationApiException> ReadExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = "installation.connection_unavailable";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("problemCode", out var problem)) code = problem.GetString() ?? code;
        }
        catch (JsonException) { }
        return new InstallationApiException(code, response.StatusCode);
    }
    private static async Task<HttpResponseMessage> DownloadHttpsAsync(HttpClient client, Uri source, CancellationToken ct)
    {
        var current = source;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode)) return response;
            var location = response.Headers.Location;
            var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
            if (next is null || next.Scheme != Uri.UriSchemeHttps)
                throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.BadGateway);
            current = next;
        }
        throw new InstallationApiException("installation.host_download_failed", HttpStatusCode.BadGateway);
    }
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
