using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Startup reconciliation for accepted configuration, installation and removal publications. It never installs, prepares,
/// adopts an old file, or generates credentials. Domain writers keep pending state blocked independently.</summary>
public sealed class MihomoPublicationRecoveryService(MihomoPublicationStore store,
    MihomoConfigurationPublicationService publication, MihomoInstallationPublicationService installation,
    MihomoRemovalPublicationService removal,
    ILogger<MihomoPublicationRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? operation = null;
            try
            {
                var state = await store.ReadAsync(stoppingToken);
                if (state.Pending is not { } pending) return;
                if (pending.ConfigurationPlan is null && pending.InstallationPlan is null && pending.RemovalPlan is null)
                {
                    logger.LogWarning("Mihomo installation publication remains pending. OperationId={OperationId} ProblemCode={ProblemCode}",
                        pending.OperationId, "runtime.installation_publication_required");
                    return;
                }
                operation = pending.OperationId;
                var result = pending.RemovalPlan is not null ? await removal.RecoverAsync(pending.OperationId, stoppingToken)
                    : pending.InstallationPlan is not null
                    ? await installation.RecoverAsync(pending.OperationId, stoppingToken)
                    : await publication.RecoverAsync(pending.OperationId, stoppingToken);
                if (result.Outcome == MihomoPublicationOutcome.RecoveryRequired)
                    logger.LogWarning("Mihomo publication needs operator recovery. OperationId={OperationId} ProblemCode={ProblemCode}",
                        operation, result.Receipt?.ProblemCode ?? "runtime.recovery_required");
                else logger.LogInformation("Mihomo publication reconciled at startup. OperationId={OperationId} Outcome={Outcome}", operation, result.Outcome);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is RuntimePolicyException or RuntimeLifecycleException or IOException or UnauthorizedAccessException)
            {
                var code = ex switch { RuntimePolicyException policy => policy.ProblemCode,
                    RuntimeLifecycleException lifecycle => lifecycle.ProblemCode, _ => "runtime.publication_recovery_failed" };
                // A live worker or temporarily unavailable Helper must not cause preparation to restart,
                // nor be mislabeled as a terminal transaction. Retry the same durable operation only.
                if (code is "runtime.instance_busy" or "runtime.helper_unavailable")
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    continue;
                }
                logger.LogWarning("Mihomo startup reconciliation left publication pending. OperationId={OperationId} ProblemCode={ProblemCode}", operation, code);
                return;
            }
        }
    }
}
