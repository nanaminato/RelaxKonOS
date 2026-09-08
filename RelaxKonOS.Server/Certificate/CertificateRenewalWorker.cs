using System.Collections.Concurrent;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Host-local renewal scheduler. Each run uses a day-scoped idempotency key so a restart
/// cannot start duplicate renewal orders; ACME Retry-After remains handled by the ACME adapter/CA.</summary>
internal sealed class CertificateRenewalWorker(ICertificateStore certificates, ICertificateManager manager, IAcmeRenewalInfoProvider renewalInfo,
    CertificateRenewalAttemptRepository renewalAttempts, CertificateOptions options, ILogger<CertificateRenewalWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextAriRefresh = new();
    private readonly ConcurrentDictionary<Guid, byte> _reportedExhaustion = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not compete with startup migrations, administrator configuration, or a manual first issuance.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        // A short scheduler interval is needed for bounded retry backoff. ARI is cached per
        // certificate for six hours, so this does not turn into a high-frequency CA poll.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RenewDueCertificatesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Certificate renewal scan failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RenewDueCertificatesAsync(CancellationToken cancellationToken)
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
            var operation = await manager.RenewAsync(certificate.Id, key, "renewal-worker", cancellationToken);
            if (operation.OperationId != Guid.Empty)
                logger.LogInformation("Queued certificate renewal. CertificateId={CertificateId} OperationId={OperationId}", certificate.Id, operation.OperationId);
        }
    }
}
