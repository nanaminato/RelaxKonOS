using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Client.Apps.FileServices;

public interface IRemoteFileServicesClient
{
    Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default);
    Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> LifecycleAsync(string action, CancellationToken ct = default);
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
    private async Task<T> Send<T>(HttpMethod method, string suffix, CancellationToken ct)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), (FileServiceApiRoutes.Smb + suffix).TrimStart('/')));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidOperationException("RelaxKonOS returned an empty response.");
    }
}
