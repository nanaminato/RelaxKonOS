using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Certificate;

internal sealed record RenewalRecoveryBaseline(string Version, string Fingerprint, string[] HostNames, bool IsDefault);
internal sealed record RenewalRecoveryPlan(Guid OperationId, Guid CertificateId, string Actor, string Target,
    RenewalRecoveryBaseline Baseline, string IssuedFingerprint);

internal sealed partial class CertificateOperationStore
{
    private sealed record KestrelRecoveryCheckpoint(RenewalRecoveryPlan Plan, string ChainHash, bool Completed = false);
    private const string KestrelRecoveryPending = "certificate.kestrel_recovery_pending";
    private readonly Dictionary<Guid, TaskCompletionSource> _kestrelRecoveryOwners = [];

    internal async Task<RenewalRecoveryBaseline?> ReadRenewalBaselineAsync(StoredCertificate current, CancellationToken ct)
    {
        if (_kestrel is null || _deployments?.IsPersistent != true || current.Kind != CertificateKind.Acme
            || current.Status == CertificateStatus.Revoked || current.FingerprintSha256 is not { } storedFingerprint)
            return null;
        var selector = _kestrel.Snapshot(current.Id);
        if (!selector.Registered || selector.FingerprintSha256 is not { } fingerprint
            || !string.Equals(fingerprint, storedFingerprint.Replace(":", ""), StringComparison.OrdinalIgnoreCase)
            || !current.Domains.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(selector.HostNames))
            return null;
        if (_certificates is not FileCertificateStore material || !await material.MatchesMaterialIndexAsync(current, ct)) return null;

        try
        {
            var deployed = (await _deployments.ListKestrelAsync(ct)).SingleOrDefault(item => item.CertificateId == current.Id);
            return deployed is not null && deployed.CurrentVersion == current.Version
                && deployed.LastSuccessfulVersion == current.Version && deployed.IsDefault == selector.IsDefault
                ? new(current.Version, fingerprint, selector.HostNames.ToArray(), selector.IsDefault) : null;
        }
        catch (SqliteException error)
        {
            _logger.LogWarning(error, "Certificate recovery binding could not be read. CertificateId={CertificateId}", current.Id);
            return null;
        }
    }

    internal async Task<CertificateOrderRecoveryPlanDto?> PlanKestrelRecoveryUnderLeaseAsync(Guid operationId, string actor,
        Func<IAcmeService> resolveAcme, CancellationToken ct)
    {
        PersistedOperation? operation;
        await _gate.WaitAsync(ct);
        try { RequireReady(); operation = _operations.GetValueOrDefault(operationId); }
        finally { _gate.Release(); }
        if (operation is null) return null;
        var receipt = operation.ToDto();
        if (operation is not { Kind: "renew", State: CertificateOperationState.Failed, ProblemCode: InterruptedProblem,
                Challenges: null, Order: { } order, ProtectedRenewalBaseline: { } protectedBaseline } || _protection is null)
            return new(receipt, "baseline-unavailable");

        var protector = OrderKeyProtector(_protection, operationId, operation.CertificateId, order);
        RenewalRecoveryBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<RenewalRecoveryBaseline>(
                protector.CreateProtector("renewal-baseline").Unprotect(protectedBaseline), Json);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { return new(receipt, "baseline-unavailable"); }
        if (baseline is null) return new(receipt, "baseline-unavailable");

        return await UnderCertificateGateAsync(operation.CertificateId, async execution =>
        {
            if (!await MatchesActiveAsync(execution)) return new(receipt, "active-version-changed");
            var verified = await VerifyOrderCertificateUnderLeaseAsync(operationId, resolveAcme, execution);
            if (verified is not { Status: "verified", FingerprintSha256: { } issued })
                return new CertificateOrderRecoveryPlanDto(receipt, verified?.Status ?? "identity-unavailable");
            if (!await MatchesActiveAsync(execution)) return new(receipt, "active-version-changed");

            const string target = "kestrel";
            var plan = new RenewalRecoveryPlan(operationId, operation.CertificateId, actor, target, baseline, issued);
            var token = protector.CreateProtector("renewal-plan").Protect(JsonSerializer.Serialize(plan, Json));
            return new(receipt, "ready", target, baseline.Version, baseline.Fingerprint, issued, token);
        }, ct);

        async Task<bool> MatchesActiveAsync(CancellationToken token)
        {
            var current = await _certificates.GetAsync(operation.CertificateId, token);
            return current is not null && await ReadRenewalBaselineAsync(current, token) is { } observed
                && observed.Version == baseline.Version && observed.Fingerprint == baseline.Fingerprint
                && observed.IsDefault == baseline.IsDefault && observed.HostNames.SequenceEqual(baseline.HostNames);
        }
    }

    internal async Task<CertificateOrderRecoveryResultDto?> ApplyKestrelRecoveryUnderLeaseAsync(Guid operationId, string actor,
        string token, Func<IAcmeService> resolveAcme, CancellationToken ct)
    {
        var original = await ReadOperationAsync(operationId, ct);
        if (original is null) return null;
        var receipt = original.ToDto();
        if (original is not { Kind: "renew", State: CertificateOperationState.Failed, ProblemCode: InterruptedProblem,
                Challenges: null, Order: { } order } || _protection is null || _kestrel is null || _deployments is null
            || _certificates is not FileCertificateStore material)
            return new(receipt, "baseline-unavailable");

        RenewalRecoveryPlan? plan;
        try
        {
            var protector = OrderKeyProtector(_protection, operationId, original.CertificateId, order).CreateProtector("renewal-plan");
            plan = token is { Length: > 0 and <= 16384 }
                ? JsonSerializer.Deserialize<RenewalRecoveryPlan>(protector.Unprotect(token), Json) : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException) { plan = null; }
        if (plan is null || plan.OperationId != operationId || plan.CertificateId != original.CertificateId
            || plan.Actor != actor || plan.Target != "kestrel") return new(receipt, "plan-invalid");

        return await UnderCertificateGateAsync(original.CertificateId, async execution =>
        {
            var operation = (await ReadOperationAsync(operationId, execution))!;
            var checkpoint = operation.KestrelRecovery;
            if (checkpoint is not null)
            {
                if (JsonSerializer.Serialize(checkpoint.Plan, Json) != JsonSerializer.Serialize(plan, Json))
                    return new CertificateOrderRecoveryResultDto(receipt, "plan-conflict");
                if (checkpoint.Completed) return new(receipt, "applied", plan.IssuedFingerprint);
                if (await IsKestrelRecoveryCommittedAsync(checkpoint, execution))
                {
                    await SaveKestrelRecoveryAsync(operation, checkpoint, completed: true);
                    return new(receipt, "applied", plan.IssuedFingerprint);
                }
            }
            var current = await _certificates.GetAsync(operation.CertificateId, execution);
            if (current is null || !MatchesBaseline(await ReadRenewalBaselineAsync(current, execution), plan.Baseline)
                || await IsRevocationBlockedAsync(operationId, operation.CertificateId))
                return new(receipt, checkpoint is null ? "active-version-changed" : "recovery-required");
            if (checkpoint is null && await IsKestrelRecoveryBlockedAsync(operation.CertificateId))
                return new(receipt, "recovery-required");

            if (checkpoint is null)
            {
                var fetched = await ReadVerifiedOrderMaterialAsync(operation, resolveAcme, execution);
                if (fetched.Proof is not { Status: "verified", FingerprintSha256: { } fingerprint }
                    || fetched.Pem is null || fetched.Key is null) return new(receipt, fetched.Proof.Status);
                if (fingerprint != plan.IssuedFingerprint) return new(receipt, "identity-mismatch");
                current = await _certificates.GetAsync(operation.CertificateId, execution);
                if (current is null || !MatchesBaseline(await ReadRenewalBaselineAsync(current, execution), plan.Baseline))
                    return new(receipt, "active-version-changed");
                execution.ThrowIfCancellationRequested();
                await material.StageKestrelRecoveryAsync(operation.CertificateId, operationId, fetched.Pem, fetched.Key, CancellationToken.None);
                checkpoint = new(plan, FileCertificateStore.PemHash(fetched.Pem));
                operation = await SaveKestrelRecoveryAsync(operation, checkpoint, completed: false);
            }

            var version = operationId.ToString("N");
            if (!await material.KestrelRecoveryChainMatchesAsync(operation.CertificateId, operationId, checkpoint.ChainHash, CancellationToken.None))
                return new(receipt, "recovery-required");
            var replacement = await material.LoadKestrelVersionAsync(operation.CertificateId, version, CancellationToken.None);
            var activated = false;
            try
            {
                if (replacement is null || replacement.TargetCertificate.GetCertHashString(HashAlgorithmName.SHA256) != plan.IssuedFingerprint)
                    return new(receipt, "recovery-required");
                activated = _kestrel.TryApplyRecovery(operation.CertificateId, plan.Baseline, replacement,
                    () => _deployments.TryCommitKestrelRecovery(operation.CertificateId, plan.Baseline, version));
                if (!activated) return new(receipt, "recovery-required");
            }
            catch (SqliteException error)
            {
                _logger.LogWarning(error, "Kestrel recovery deployment is unconfirmed. OperationId={OperationId}", operationId);
                return new(receipt, "recovery-required");
            }
            finally { if (!activated) replacement?.TargetCertificate.Dispose(); }

            await SaveKestrelRecoveryAsync(operation, checkpoint, completed: true);
            return new(receipt, "applied", plan.IssuedFingerprint);
        }, ct);
    }

    private async Task<PersistedOperation?> ReadOperationAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireReady(); return _operations.GetValueOrDefault(id); }
        finally { _gate.Release(); }
    }

    private async Task<PersistedOperation> SaveKestrelRecoveryAsync(PersistedOperation operation, KestrelRecoveryCheckpoint checkpoint, bool completed)
    {
        using var responsibility = completed ? null : await _work.AdmitAsync(CancellationToken.None);
        await _gate.WaitAsync();
        try
        {
            var updated = completed ? CompleteKestrelRecovery(operation, checkpoint) : operation with { KestrelRecovery = checkpoint };
            _operations[operation.OperationId] = updated;
            try { await SaveLedgerAsync(CancellationToken.None); }
            catch { _operations[operation.OperationId] = operation; throw; }
            if (responsibility is not null) HoldKestrelRecovery(operation.OperationId, responsibility);
            else if (_kestrelRecoveryOwners.Remove(operation.OperationId, out var terminal)) terminal.TrySetResult();
            return updated;
        }
        finally { _gate.Release(); }
    }

    private void HoldKestrelRecovery(Guid operationId, ResidentBackgroundWorkOwner.Work responsibility)
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _kestrelRecoveryOwners.Add(operationId, terminal);
        responsibility.Run(async stopping =>
        {
            try { await terminal.Task.WaitAsync(stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        });
    }

    private static PersistedOperation CompleteKestrelRecovery(PersistedOperation operation, KestrelRecoveryCheckpoint checkpoint) =>
        operation with { KestrelRecovery = checkpoint with { Completed = true }, ProtectedOrderKey = null, ProtectedRenewalBaseline = null };

    private async Task<bool> IsKestrelRecoveryCommittedAsync(KestrelRecoveryCheckpoint checkpoint, CancellationToken ct)
    {
        if (_kestrel is null || _deployments is null || _certificates is not FileCertificateStore material) return false;
        var plan = checkpoint.Plan;
        var live = _kestrel.Snapshot(plan.CertificateId);
        if (!live.Registered || live.FingerprintSha256 != plan.IssuedFingerprint || live.IsDefault != plan.Baseline.IsDefault
            || !live.HostNames.SequenceEqual(plan.Baseline.HostNames)) return false;
        var version = plan.OperationId.ToString("N");
        var deployed = (await _deployments.ListKestrelAsync(ct)).SingleOrDefault(item => item.CertificateId == plan.CertificateId);
        return deployed is not null && deployed.CurrentVersion == version && deployed.LastSuccessfulVersion == version
            && deployed.IsDefault == plan.Baseline.IsDefault
            && await material.KestrelRecoveryChainMatchesAsync(plan.CertificateId, plan.OperationId, checkpoint.ChainHash, ct);
    }

    private static bool MatchesBaseline(RenewalRecoveryBaseline? observed, RenewalRecoveryBaseline expected) =>
        observed is not null && observed.Version == expected.Version && observed.Fingerprint == expected.Fingerprint
        && observed.IsDefault == expected.IsDefault && observed.HostNames.SequenceEqual(expected.HostNames);

    private bool HasPendingKestrelRecovery(Guid certificateId) => _operations.Values.Any(item =>
        item.CertificateId == certificateId && item.KestrelRecovery is { Completed: false });

    private static CertificateOperationDto KestrelRecoveryConflict(Guid certificateId, string kind) =>
        new(Guid.Empty, certificateId, kind, CertificateOperationState.Failed, "failed", KestrelRecoveryPending, null, DateTimeOffset.UtcNow);

    private async Task<bool> IsKestrelRecoveryBlockedAsync(Guid certificateId)
    {
        await _gate.WaitAsync();
        try { return HasPendingKestrelRecovery(certificateId); }
        finally { _gate.Release(); }
    }
}
