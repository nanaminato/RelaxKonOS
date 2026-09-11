using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Client.Apps.FileServices;

public interface IRemoteFileServicesClient
{
    Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct = default);
    Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct = default);
    Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct = default);
    Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username, SetSambaPasswordRequest request, CancellationToken ct = default);
    Task<bool> ElevateAsync(string password, CancellationToken ct = default);
}

