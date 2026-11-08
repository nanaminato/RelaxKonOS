using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal sealed record PendingNginxRenewal(Guid OperationId, Guid CertificateId, string Version, string[]? Targets);

internal sealed partial class CertificateOperationStore
{
    private sealed record NginxRenewalCheckpoint(string Version, string[]? Targets = null);
    private readonly SemaphoreSlim _nginxRenewalsChanged = new(0, 1);

    internal Task<CertificateOperationDto> StartRenewalAsync(string key, Guid certificateId, string? actor,
        Func<Func<AcmeOrderIdentity, Task>, Func<WebRootChallengeFile, Task>, CancellationToken, Task<(string Problem, string? Version)>> action,
        CancellationToken stopping, CancellationToken admission, bool automatic) =>
        StartCoreAsync(key, certificateId, "renew", actor, async (id, ct) =>
        {
            var result = await action(identity => RecordOrderAsync(id, identity), file => RecordChallengeAsync(id, file), ct);
            if (_deployments?.IsPersistent == true && result is { Problem.Length: 0, Version: { } version })
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { _operations[id] = _operations[id] with { NginxRenewal = new(version) }; }
                finally { _gate.Release(); }
            }
            return result.Problem;
        }, stopping, admission, automatic);

    internal Task WaitForNginxRenewalsAsync(TimeSpan delay, CancellationToken ct) => _nginxRenewalsChanged.WaitAsync(delay, ct);

    internal async Task<IReadOnlyList<PendingNginxRenewal>> PendingNginxRenewalsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireReady();
            return _operations.Values.Where(item => item.State == CertificateOperationState.Succeeded && item.NginxRenewal is not null)
                .OrderBy(item => item.CompletedAt)
                .GroupBy(item => item.CertificateId).Select(group => group.First())
                .Select(item => new PendingNginxRenewal(item.OperationId, item.CertificateId, item.NginxRenewal!.Version, item.NginxRenewal.Targets))
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    internal async Task SaveNginxRenewalTargetsAsync(PendingNginxRenewal work, string[] targets, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[work.OperationId];
            _operations[work.OperationId] = operation with
            {
                NginxRenewal = targets.Length == 0 ? null : new(work.Version, targets)
            };
            try { await SaveLedgerAsync(CancellationToken.None); }
            catch { _operations[work.OperationId] = operation; throw; }
        }
        finally { _gate.Release(); }
    }

    internal async Task<T> UnderCertificateGateAsync<T>(Guid certificateId, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var gate = _certificateGates.GetOrAdd(certificateId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(ct); }
        finally { gate.Release(); }
    }
}
