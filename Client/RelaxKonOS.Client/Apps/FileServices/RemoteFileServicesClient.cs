using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Typed protocol-only SMB client. It has no native service, Helper, or credential knowledge.</summary>
public sealed class RemoteFileServicesClient(HttpClient http, IAuthSession session) : IRemoteFileServicesClient
{
    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default) => Send<FileServiceStatusDto>(HttpMethod.Get, FileServiceApiRoutes.Status, ct);
    public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default) => Send<FileServiceCapabilitiesDto>(HttpMethod.Get, FileServiceApiRoutes.Capabilities, ct);
    public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default) => Send<IReadOnlyList<FileShareDto>>(HttpMethod.Get, FileServiceApiRoutes.Shares, ct);
    public Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default) => Send<FileServiceConnectionInfoDto>(HttpMethod.Get, FileServiceApiRoutes.Connection, ct);
    public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, action switch { SmbLifecycleAction.Start => FileServiceApiRoutes.Start, SmbLifecycleAction.Stop => FileServiceApiRoutes.Stop, SmbLifecycleAction.Restart => FileServiceApiRoutes.Restart, _ => throw new ArgumentOutOfRangeException(nameof(action)) }, ct);
    public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, FileServiceApiRoutes.Shares, ct, request);
    public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Put, FileServiceApiRoutes.ShareById.Replace("{shareId}", Uri.EscapeDataString(id), StringComparison.Ordinal), ct, request);
    public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Delete, FileServiceApiRoutes.ShareById.Replace("{shareId}", Uri.EscapeDataString(id), StringComparison.Ordinal), ct);
    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default) => Send<IReadOnlyList<FileServiceUserDto>>(HttpMethod.Get, FileServiceApiRoutes.Users, ct);
    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, (enabled ? FileServiceApiRoutes.EnableUser : FileServiceApiRoutes.DisableUser).Replace("{username}", Uri.EscapeDataString(username), StringComparison.Ordinal), ct);
    public Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username, SetSambaPasswordRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Put, FileServiceApiRoutes.UserPassword.Replace("{username}", Uri.EscapeDataString(username), StringComparison.Ordinal), ct, request);
    public async Task<bool> ElevateAsync(string password, CancellationToken ct = default)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(session.ServerUrl), PrivilegedApiRoutes.Elevation.TrimStart('/')))
        { Content = JsonContent.Create(new HostElevationRequest(HostElevationCapability.SmbManage, "smb:managed", password), options: RelaxKonOSJsonOptions.Default) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<HostElevationResult>(RelaxKonOSJsonOptions.Default, ct))?.Elevated == true;
    }
    private async Task<T> Send<T>(HttpMethod method, string route, CancellationToken ct, object? body = null)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        { Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }

    private static async Task<HttpRequestException> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("problemCode", out var code)
                && code.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(code.GetString()))
                return new FileServiceApiException(code.GetString()!, response.StatusCode);
            // ASP.NET Core Problem Details communicates this endpoint's problem code in the
            // final path segment of `type` (for example, .../elevation-password-invalid).
            // File Services must retain that code so the UI can distinguish a bad password
            // from other authorization and transport failures.
            if (document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && TryGetProblemCode(type.GetString(), out var problemCode))
                return new FileServiceApiException(problemCode, response.StatusCode);
        }
        catch (JsonException) { }
        return new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
    }

    private static bool TryGetProblemCode(string? type, out string problemCode)
    {
        problemCode = string.Empty;
        if (string.IsNullOrWhiteSpace(type)) return false;
        var slash = type.LastIndexOf('/');
        var value = slash >= 0 ? type[(slash + 1)..] : type;
        if (string.IsNullOrWhiteSpace(value)) return false;
        problemCode = value;
        return true;
    }
}

internal sealed class FileServiceApiException(string problemCode, HttpStatusCode statusCode) : HttpRequestException(problemCode, null, statusCode)
{
    public string ProblemCode { get; } = problemCode;
}
