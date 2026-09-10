using System.Net.Http.Json;
using System.Net;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Apps.FileServices;

public interface IRemoteFileServicesClient
{
    Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default);
    Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> LifecycleAsync(string action, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username, SetSambaPasswordRequest request, CancellationToken ct = default);
    Task<bool> ElevateAsync(string password, CancellationToken ct = default);
}

/// <summary>Typed protocol-only SMB client. It has no native service, Helper, or credential knowledge.</summary>
public sealed class RemoteFileServicesClient(HttpClient http, IAuthSession session) : IRemoteFileServicesClient
{
    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default) => Send<FileServiceStatusDto>(HttpMethod.Get, "/status", ct);
    public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default) => Send<FileServiceCapabilitiesDto>(HttpMethod.Get, "/capabilities", ct);
    public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default) => Send<IReadOnlyList<FileShareDto>>(HttpMethod.Get, "/shares", ct);
    public Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default) => Send<FileServiceConnectionInfoDto>(HttpMethod.Get, "/connection", ct);
    public Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, "/install", ct);
    public Task<FileServiceOperationResultDto> LifecycleAsync(string action, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, "/" + action, ct);
    public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, "/shares", ct, request);
    public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Put, "/shares/" + WebUtility.UrlEncode(id), ct, request);
    public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Delete, "/shares/" + WebUtility.UrlEncode(id), ct);
    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default) => Send<IReadOnlyList<FileServiceUserDto>>(HttpMethod.Get, "/users", ct);
    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Post, "/users/" + WebUtility.UrlEncode(username) + (enabled ? "/enable" : "/disable"), ct);
    public Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username, SetSambaPasswordRequest request, CancellationToken ct = default) => Send<FileServiceOperationResultDto>(HttpMethod.Put, "/users/" + WebUtility.UrlEncode(username) + "/password", ct, request);
    public async Task<bool> ElevateAsync(string password, CancellationToken ct = default)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(session.ServerUrl), PrivilegedApiRoutes.Elevation.TrimStart('/')))
        { Content = JsonContent.Create(new HostElevationRequest(HostElevationCapability.SmbManage, "smb:managed", password), options: RelaxKonOSJsonOptions.Default) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        return (await response.Content.ReadFromJsonAsync<HostElevationResult>(RelaxKonOSJsonOptions.Default, ct))?.Elevated == true;
    }
    private async Task<T> Send<T>(HttpMethod method, string suffix, CancellationToken ct, object? body = null)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), (FileServiceApiRoutes.Smb + suffix).TrimStart('/')))
        { Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }
}
