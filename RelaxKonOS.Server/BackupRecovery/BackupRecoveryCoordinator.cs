using Microsoft.Extensions.Hosting;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>
/// Startup reconciliation for durable backup work. The service never replays a write after a
/// process crash: the object may have been written but not verified, so an operator must begin a
/// fresh idempotent request after inspecting the interrupted record.
/// </summary>
internal sealed class BackupRecoveryCoordinator(BackupRecoveryManifestStore manifests) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        manifests.MarkInterruptedAtStartup();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
