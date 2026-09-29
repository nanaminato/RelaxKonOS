using Microsoft.Extensions.Hosting;

namespace RelaxKonOS.Server.BackupRecovery;

/// <summary>
/// Startup reconciliation for durable backup work. The service never replays a write after a
/// process crash: the object may have been written but not verified, so an operator must begin a
/// fresh idempotent request after inspecting the interrupted record.
/// </summary>
internal sealed class BackupRecoveryCoordinator(
    BackupRecoveryManifestStore manifests,
    BackupRecoveryObjectStore objects,
    BackupRecoveryOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        manifests.MarkInterruptedAtStartup();
        manifests.ApplyVerifiedRetention(options.MaximumVerifiedBackupsPerApplication);
        await objects.ReconcileAtStartupAsync(manifests.VerifiedBackupIds(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
