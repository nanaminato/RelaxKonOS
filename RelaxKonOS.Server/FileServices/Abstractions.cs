using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Server.FileServices;

public interface IFileServiceProvider
{
    FileServiceProtocol Protocol { get; }
    bool IsApplicable { get; }
    Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct);
    Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> InstallAsync(Guid operationId, CancellationToken ct);
    Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid operationId, CancellationToken ct);
    Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, Guid operationId, CancellationToken ct);
    Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, Guid operationId, CancellationToken ct);
    Task<FileServiceOperationResultDto> DeleteShareAsync(string id, Guid operationId, CancellationToken ct);
    Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, Guid operationId, CancellationToken ct);
    Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, Guid operationId, CancellationToken ct);
}

public interface IFileServiceProviderResolver { IFileServiceProvider? Resolve(FileServiceProtocol protocol); }

public interface IFileServiceManager
{
    Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct);
    Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct);
    Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct);
    Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct);
    Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct);
    Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct);
    Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, CancellationToken ct);
    FileServiceConnectionInfoDto GetConnectionInfo();
}

public enum SmbLifecycleAction { Start, Stop, Restart }
