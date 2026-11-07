using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Startup reconciliation for accepted configuration, installation and removal publications. It never installs, prepares,
/// adopts an old file, or generates credentials. Domain writers keep pending state blocked independently.</summary>
public sealed class MihomoPublicationRecoveryService(IServiceScopeFactory scopes,
    ILogger<MihomoPublicationRecoveryService> logger)
{
    public async Task<Guid?> ReconcileAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await ReconcilePassAsync(stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            if (!result.Retry) return result.Blocked;
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        stoppingToken.ThrowIfCancellationRequested();
        return null;
    }

    // Completed scopes and coordinator locals never live in the retry timer's state machine.
    private async Task<(bool Retry, Guid? Blocked)> ReconcilePassAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<MihomoPublicationStore>();
        Guid? operation = null;
        try
        {
            var state = await store.ReadAsync(stoppingToken);
            if (state.DirectReload is { } direct)
            {
                operation = direct.OperationId;
                await store.RestoreDirectConfigurationAsync(direct.OperationId, stoppingToken);
                logger.LogWarning("Mihomo direct reload outcome requires verification. OperationId={OperationId}", direct.OperationId);
                return (false, operation);
            }
            if (state.Pending is not { } pending) return (false, null);
            if (pending.ConfigurationPlan is null && pending.InstallationPlan is null && pending.RemovalPlan is null)
            {
                logger.LogWarning("Mihomo installation publication remains pending. OperationId={OperationId} ProblemCode={ProblemCode}",
                    pending.OperationId, "runtime.installation_publication_required");
                return (false, pending.OperationId);
            }
            operation = pending.OperationId;
            var result = pending.RemovalPlan is not null ? await scope.ServiceProvider.GetRequiredService<MihomoRemovalPublicationService>().RecoverAsync(pending.OperationId, stoppingToken)
                : pending.InstallationPlan is not null
                ? await scope.ServiceProvider.GetRequiredService<MihomoInstallationPublicationService>().RecoverAsync(pending.OperationId, stoppingToken)
                : await scope.ServiceProvider.GetRequiredService<MihomoConfigurationPublicationService>().RecoverAsync(pending.OperationId, stoppingToken);
            if (result.Outcome == MihomoPublicationOutcome.RecoveryRequired)
                logger.LogWarning("Mihomo publication needs operator recovery. OperationId={OperationId} ProblemCode={ProblemCode}",
                    operation, result.Receipt?.ProblemCode ?? "runtime.recovery_required");
            else logger.LogInformation("Mihomo publication reconciled at startup. OperationId={OperationId} Outcome={Outcome}", operation, result.Outcome);
            return (false, result.Outcome == MihomoPublicationOutcome.RecoveryRequired ? pending.OperationId : null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return (false, null); }
        catch (Exception ex) when (ex is RuntimePolicyException or RuntimeLifecycleException or IOException or UnauthorizedAccessException)
        {
            var code = ex switch { RuntimePolicyException policy => policy.ProblemCode,
                RuntimeLifecycleException lifecycle => lifecycle.ProblemCode, _ => "runtime.publication_recovery_failed" };
            // A live worker or temporarily unavailable Helper must not cause preparation to restart,
            // nor be mislabeled as a terminal transaction. Retry the same durable operation only.
            if (code is "runtime.instance_busy" or "runtime.helper_unavailable")
            {
                return (true, null);
            }
            logger.LogWarning("Mihomo startup reconciliation left publication pending. OperationId={OperationId} ProblemCode={ProblemCode}", operation, code);
            return (false, operation);
        }
    }
}
