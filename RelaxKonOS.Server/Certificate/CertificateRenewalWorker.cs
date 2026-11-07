using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Waits on durable scan state without holding a renewal execution scope.</summary>
internal sealed class CertificateRenewalWorker(CertificateRenewalRunRepository runs, IServiceScopeFactory scopes,
    IResidentModuleWorkLeaseSource leases, ILogger<CertificateRenewalWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupAndRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupAndRetryDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await runs.NextScanAsync(stoppingToken);
                var delay = next - DateTimeOffset.UtcNow ?? TimeSpan.Zero;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, stoppingToken);
                else
                    await RunDueScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Certificate renewal scan remains pending.");
                await Task.Delay(StartupAndRetryDelay, stoppingToken);
            }
        }
    }

    internal async Task RunDueScanAsync(CancellationToken ct)
    {
        using var work = await leases.AcquireAsync(ServerModules.CertificatesId, ct);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, work.Stopping);
        stopping.Token.ThrowIfCancellationRequested();
        Guid? scanned;
        await using (var scope = scopes.CreateAsyncScope())
            scanned = await scope.ServiceProvider.GetRequiredService<CertificateRenewalScan>().RunAsync(stopping.Token);
        stopping.Token.ThrowIfCancellationRequested();
        if (scanned is { } id)
            await runs.EndAsync(id, DateTimeOffset.UtcNow, CancellationToken.None);
    }
}

/// <summary>Executes one durable scan. CertificateOperationStore owns each admitted renewal.</summary>
internal sealed class CertificateRenewalScan(ICertificateStore certificates, ICertificateManager manager, IAcmeRenewalInfoProvider renewalInfo,
    CertificateRenewalAttemptRepository renewalAttempts, CertificateRenewalRunRepository runs, CertificateOperationStore operations,
    CertificateOptions options, ILogger<CertificateRenewalScan> logger)
{
    internal async Task<Guid?> RunAsync(CancellationToken ct)
    {
        var slot = await runs.BeginDueAsync(DateTimeOffset.UtcNow, ct);
        if (slot is null) return null;

        // A crash can leave an admitted operation in its own ledger before the scan links it.
        // Recover that association even if the certificate was removed or no longer looks due.
        var known = await operations.GetRenewalsForScanAsync(slot.RequestKey, slot.Operations, ct);
        var missing = known.Select(operation => operation.OperationId).Except(slot.Operations).ToArray();
        if (missing.Length != 0) await runs.AttachAsync(slot.Id, missing, ct);
        var handled = known.Select(operation => operation.CertificateId.GetValueOrDefault()).Concat(slot.RejectedCertificates).ToHashSet();
        var threshold = DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.RenewalFallbackDays, 1, 90));
        foreach (var certificate in await certificates.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (handled.Contains(certificate.Id) || certificate.Kind == CertificateKind.SelfSigned
                || certificate.Status == CertificateStatus.Revoked || string.IsNullOrWhiteSpace(certificate.ContactEmail)) continue;

            var now = DateTimeOffset.UtcNow;
            var ariWindow = certificate.RenewalWindowStart is { } start && certificate.RenewalWindowEnd is { } end
                ? new AcmeRenewalWindow(start, end)
                : null;
            using (var material = await certificates.LoadCurrentAsync(certificate.Id, ct))
                if (material is not null)
                    ariWindow = await renewalInfo.GetRenewalWindowAsync(material, certificate.ContactEmail, ct);
            if (ariWindow is not null && ariWindow.End >= ariWindow.Start)
                await certificates.UpdateRenewalWindowAsync(certificate.Id, ariWindow, ct);
            var due = ariWindow is { } window && now <= window.End
                ? now >= window.Start
                : certificate.NotAfter <= threshold;
            if (!due) continue;
            var retry = await renewalAttempts.GetScheduleAsync(certificate.Id, ct);
            if (retry.Exhausted)
            {
                logger.LogError("Certificate renewal retry limit reached. CertificateId={CertificateId} Failures={Failures}", certificate.Id, retry.ConsecutiveFailures);
                continue;
            }
            if (retry.RetryAfter is { } retryAfter && retryAfter > now) continue;

            var operation = await manager.RenewAsync(certificate.Id, slot.RequestKey, CertificateOperationStore.AutomaticRenewalActor, ct, automatic: true);
            await runs.AddAsync(slot.Id, certificate.Id, operation.OperationId, ct);
            if (operation.ProblemCode == CertificateOperationStore.InterruptedProblem)
                logger.LogWarning("Automatic certificate renewal remains blocked by an interrupted operation. CertificateId={CertificateId} OperationId={OperationId}",
                    certificate.Id, operation.OperationId);
            else if (operation.OperationId != Guid.Empty)
                logger.LogInformation("Observed certificate renewal. CertificateId={CertificateId} OperationId={OperationId} State={State}",
                    certificate.Id, operation.OperationId, operation.State);
        }
        return slot.Id;
    }
}
