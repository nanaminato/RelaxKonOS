using RelaxKonOS.Server.Installations;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Runtimes;

public interface IRuntimeManager
{
    Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string executablePath, CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken cancellationToken);
    Task<TunnelRuntimeDto> GetManagedFrpsStatusAsync(CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, IInstallationProgress progress, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> UninstallManagedFrpcAsync(CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken cancellationToken);
}
