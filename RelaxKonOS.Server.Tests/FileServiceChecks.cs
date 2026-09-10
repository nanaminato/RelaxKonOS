using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.FileServices;

public static class FileServiceChecks
{
    public static async Task RunAsync()
    {
        var provider = new FakeProvider();
        var manager = new FileServiceManager(new FileServiceProviderResolver([provider]));
        var status = await manager.GetStatusAsync(CancellationToken.None);
        Check(status.Protocol == FileServiceProtocol.Smb && status.State == FileServiceRuntimeState.Running, "SMB resolver selects the applicable provider");
        var connection = manager.GetConnectionInfo();
        Check(connection.Port == 445 && connection.WindowsUncPrefix.StartsWith("\\\\", StringComparison.Ordinal) && connection.SmbUriPrefix.StartsWith("smb://", StringComparison.Ordinal), "Connection information exposes only SMB host/port prefixes");
        var unsupported = new FileServiceManager(new FileServiceProviderResolver([]));
        var unsupportedStatus = await unsupported.GetStatusAsync(CancellationToken.None);
        Check(unsupportedStatus.State == FileServiceRuntimeState.Unsupported && unsupportedStatus.HealthProblemCode == FileServiceProblemCodes.UnsupportedPlatform, "Missing provider fails closed as unsupported platform");
        var result = await manager.LifecycleAsync(SmbLifecycleAction.Restart, CancellationToken.None);
        Check(result.Succeeded && provider.LifecycleCalls == 1, "Manager dispatches lifecycle through provider abstraction");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS FILE SERVICES: " + message);
    }
    private sealed class FakeProvider : IFileServiceProvider
    {
        public int LifecycleCalls { get; private set; }
        public FileServiceProtocol Protocol => FileServiceProtocol.Smb;
        public bool IsApplicable => true;
        public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) => Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, "fake", true, true));
        public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new FileServiceCapabilitiesDto(true, false, false, true, false));
        public Task<FileServiceOperationResultDto> InstallAsync(Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, Guid id, CancellationToken ct) { LifecycleCalls++; return Task.FromResult(new FileServiceOperationResultDto(id, true)); }
        public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileShareDto>>([]);
        public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, Guid operationId, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, Guid operationId, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(operationId, true));
        public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileServiceUserDto>>([]);
        public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
        public Task<FileServiceOperationResultDto> SetUserPasswordAsync(string username, string password, Guid id, CancellationToken ct) => Task.FromResult(new FileServiceOperationResultDto(id, true));
    }
}
