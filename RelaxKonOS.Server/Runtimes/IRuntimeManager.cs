using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Runtimes;

public interface IRuntimeManager
{
    Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string executablePath, CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpsStatusAsync(CancellationToken cancellationToken);
    TunnelRuntimeInstallationDto GetManagedFrpcInstallationStatus();
    Task<TunnelRuntimeDownloadDto?> GetManagedFrpcDownloadAsync(string version, CancellationToken ct);
    Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> InstallManagedFrpcFromArchiveAsync(string version, string archivePath, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> UninstallManagedFrpcAsync(CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken cancellationToken);
}
