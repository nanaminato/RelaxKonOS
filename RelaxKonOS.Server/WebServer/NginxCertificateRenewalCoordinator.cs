using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.WebServer;

/// <summary>Projects successful certificate renewals through the existing Nginx operation owner.</summary>
internal sealed class NginxCertificateRenewalCoordinator(CertificateOperationStore certificates,
    CertificateDeploymentRepository deployments, NginxWebServerManager nginx, IResidentModuleWorkLeaseSource leases,
    ILogger<NginxCertificateRenewalCoordinator> logger) : BackgroundService
{
    public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!deployments.IsPersistent) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Timeout.InfiniteTimeSpan;
            try
            {
                var pending = await certificates.PendingNginxRenewalsAsync(stoppingToken);
                if (pending.Count != 0)
                {
                    delay = TimeSpan.FromSeconds(2);
                    using var lease = await leases.AcquireAsync(ServerModules.WebServerId, stoppingToken);
                    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Stopping);
                    foreach (var renewal in pending)
                        await ReconcileAsync(renewal, stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                delay = TimeSpan.FromSeconds(5);
                logger.LogWarning(error, "Nginx certificate deployment will be reconciled from the original renewal receipt.");
            }
            await certificates.WaitForNginxRenewalsAsync(delay, stoppingToken);
        }
    }

    private async Task ReconcileAsync(PendingNginxRenewal renewal, CancellationToken ct)
    {
        var targets = renewal.Targets;
        if (targets is null)
        {
            targets = await nginx.GetCertificateReloadTargetsAsync(renewal.CertificateId, ct);
            await certificates.SaveNginxRenewalTargetsAsync(renewal, targets, ct);
        }

        var remaining = new List<string>();
        foreach (var target in targets)
        {
            var operation = await nginx.StartCertificateReloadAsync(renewal, target, certificates, ct);
            var pending = operation.State is WebServerOperationState.Queued or WebServerOperationState.Running
                || operation.OperationId == Guid.Empty && operation.ProblemCode == WebServerOperationStore.NativeRecoveryRequired;
            if (pending) remaining.Add(target);
            var succeeded = operation.State == WebServerOperationState.Succeeded;
            var problem = succeeded ? null : pending
                ? operation.ProblemCode == WebServerOperationStore.NativeRecoveryRequired
                    ? operation.ProblemCode : "certificate.nginx_reload_pending"
                : "certificate.nginx_reload_failed";
            await deployments.RecordNginxRenewalAsync(renewal, target,
                operation.OperationId == Guid.Empty ? null : operation.OperationId, succeeded, problem, ct);
        }
        if (remaining.Count != targets.Length)
            await certificates.SaveNginxRenewalTargetsAsync(renewal, remaining.ToArray(), ct);
    }
}
