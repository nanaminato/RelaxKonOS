using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.Server.FileServices;

/// <summary>Protocol-neutral orchestration. Every mutation is serialized per protocol before it reaches a host adapter.</summary>
public sealed class FileServiceManager(IFileServiceProviderResolver resolver) : IFileServiceManager
{
    private static readonly SemaphoreSlim SmbGate = new(1, 1);
    private IFileServiceProvider? Provider => resolver.Resolve(FileServiceProtocol.Smb);
    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) => Provider?.GetStatusAsync(ct) ?? Task.FromResult(UnsupportedStatus());
    public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct) => Provider?.GetCapabilitiesAsync(ct) ?? Task.FromResult(UnsupportedCapabilities());
    public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct) => Provider?.ListSharesAsync(ct) ?? Task.FromResult<IReadOnlyList<FileShareDto>>([]);
    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) => Provider?.ListUsersAsync(ct) ?? Task.FromResult<IReadOnlyList<FileServiceUserDto>>([]);
    public Task<FileServiceOperationResultDto> InstallAsync(CancellationToken ct) => Mutate(p => p.InstallAsync(Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct) => Mutate(p => p.LifecycleAsync(action, Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct) => Mutate(p => p.CreateShareAsync(request, Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct) => Mutate(p => p.UpdateShareAsync(id, request, Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct) => Mutate(p => p.DeleteShareAsync(id, Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct) => Mutate(p => p.SetUserEnabledAsync(username, enabled, Guid.NewGuid(), ct), ct);
    public Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, CancellationToken ct) => Mutate(p => p.SetUserPasswordAsync(username, password, Guid.NewGuid(), ct), ct);
    public FileServiceConnectionInfoDto GetConnectionInfo()
    {
        var host = Environment.MachineName;
        return new(host, 445, $@"\\{host}\", $"smb://{host}/");
    }
    private async Task<FileServiceOperationResultDto> Mutate(Func<IFileServiceProvider, Task<FileServiceOperationResultDto>> action, CancellationToken ct)
    {
        await SmbGate.WaitAsync(ct);
        try { return Provider is { } provider ? await action(provider) : Failed(); }
        finally { SmbGate.Release(); }
    }
    private static FileServiceStatusDto UnsupportedStatus() => new(FileServiceProtocol.Smb, FileServiceRuntimeState.Unsupported, null, false, false, FileServiceProblemCodes.UnsupportedPlatform);
    private static FileServiceCapabilitiesDto UnsupportedCapabilities() => new(false, false, false, false, false, FileServiceProblemCodes.UnsupportedPlatform);
    private static FileServiceOperationResultDto Failed() => new(Guid.NewGuid(), false, FileServiceProblemCodes.UnsupportedPlatform);
}
