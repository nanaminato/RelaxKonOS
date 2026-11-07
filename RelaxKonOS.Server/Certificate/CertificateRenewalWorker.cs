using System.Collections.Concurrent;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Host-local renewal scheduler. Each run uses a day-scoped idempotency key so a restart
/// cannot start duplicate renewal orders within a day. Unknown outcomes remain in the operation ledger across daily scans.</summary>
internal sealed class CertificateRenewalWorker(ICertificateStore certificates, ICertificateManager manager, IAcmeRenewalInfoProvider renewalInfo,
    CertificateRenewalAttemptRepository renewalAttempts, CertificateRenewalRunRepository runs, CertificateOptions options, ILogger<CertificateRenewalWorker> logger,
    RelaxKonOS.Server.Residency.IResidentModuleWorkLeaseSource leases) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextAriRefresh = new();
    private readonly ConcurrentDictionary<Guid, byte> _reportedExhaustion = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not compete with startup migrations, administrator configuration, or a manual first issuance.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        // Renewal is a daily maintenance task. ARI is still cached per certificate for six
        // hours, which matters when a host is restarted before the next regular scan.
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try { await RenewDueCertificatesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Certificate renewal scan failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RenewDueCertificatesAsync(CancellationToken cancellationToken)
    {
        using var work = await leases.AcquireAsync(RelaxKonOS.Server.Startup.ServerModules.CertificatesId, cancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.Stopping);
        cancellationToken = stopping.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var runId = await runs.BeginAsync(cancellationToken);
        try
        {
            var threshold = DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.RenewalFallbackDays, 1, 90));
            foreach (var certificate in await certificates.ListAsync(cancellationToken))
            {
                if (certificate.Kind == RelaxKonOS.Protocol.Certificates.CertificateKind.SelfSigned) continue;
                if (certificate.Status == RelaxKonOS.Protocol.Certificates.CertificateStatus.Revoked) continue;
                if (string.IsNullOrWhiteSpace(certificate.ContactEmail)) continue;
                var now = DateTimeOffset.UtcNow;
                var ariWindow = certificate.RenewalWindowStart is { } start && certificate.RenewalWindowEnd is { } end
                    ? new AcmeRenewalWindow(start, end)
                    : null;
                if (_nextAriRefresh.GetOrAdd(certificate.Id, DateTimeOffset.MinValue) <= now)
                {
                    using var material = await certificates.LoadCurrentAsync(certificate.Id, cancellationToken);
                    if (material is not null)
                        ariWindow = await renewalInfo.GetRenewalWindowAsync(material, certificate.ContactEmail, cancellationToken);
                    _nextAriRefresh[certificate.Id] = now.AddHours(6);
                }
                if (ariWindow is not null && ariWindow.End >= ariWindow.Start)
                    await certificates.UpdateRenewalWindowAsync(certificate.Id, ariWindow, cancellationToken);
                // ARI controls scheduling while its window remains usable. A stale/expired window
                // must never suppress the explicit NotAfter safety deadline.
                var due = ariWindow is { } window && now <= window.End
                    ? now >= window.Start
                    : certificate.NotAfter <= threshold;
                if (!due) continue;
                var retry = await renewalAttempts.GetScheduleAsync(certificate.Id, cancellationToken);
                if (retry.Exhausted)
                {
                    if (_reportedExhaustion.TryAdd(certificate.Id, 0))
                        logger.LogError("Certificate renewal retry limit reached. CertificateId={CertificateId} Failures={Failures}", certificate.Id, retry.ConsecutiveFailures);
                    continue;
                }
                if (retry.RetryAfter is { } retryAfter && retryAfter > now) continue;
                _reportedExhaustion.TryRemove(certificate.Id, out _);
                var key = $"background-{DateTimeOffset.UtcNow:yyyyMMdd}-{retry.ConsecutiveFailures}";
                RelaxKonOS.Protocol.Certificates.CertificateOperationDto operation;
                try { operation = await manager.RenewAsync(certificate.Id, key, CertificateOperationStore.AutomaticRenewalActor, cancellationToken, automatic: true); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    await runs.AddAsync(runId, Guid.Empty, cancellationToken);
                    logger.LogError(error, "Certificate renewal could not be queued. CertificateId={CertificateId}", certificate.Id);
                    continue;
                }
                await runs.AddAsync(runId, operation.OperationId, cancellationToken);
                if (operation.ProblemCode == CertificateOperationStore.InterruptedProblem)
                    logger.LogWarning("Automatic certificate renewal remains blocked by an interrupted operation. CertificateId={CertificateId} OperationId={OperationId}",
                        certificate.Id, operation.OperationId);
                else if (operation.OperationId != Guid.Empty)
                    logger.LogInformation("Observed certificate renewal. CertificateId={CertificateId} OperationId={OperationId} State={State}",
                        certificate.Id, operation.OperationId, operation.State);
            }
        }
        finally { await runs.EndAsync(runId, CancellationToken.None); }
    }
}
