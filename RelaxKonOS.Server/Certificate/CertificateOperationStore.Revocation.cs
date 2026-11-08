using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Certificate;

internal sealed partial class CertificateOperationStore
{
    private const string RevocationRecoveryProblem = "certificate.revocation_recovery_required";
    private const string RevocationCleanupProblem = "certificate.revocation_cleanup_required";
    private readonly KestrelCertificateRegistry? _kestrel;
    private readonly CertificateDeploymentRepository? _deployments;

    private sealed record RevocationCheckpoint(string Version, string Sha256, bool Confirmed = false);

    internal Task<CertificateOperationDto> StartRevocationAsync(string key, Guid certificateId, string? actor,
        Func<X509Certificate2, string, CancellationToken, Task> revoke, CancellationToken stopping, CancellationToken admission) =>
        StartCoreAsync(key, certificateId, "revoke", actor,
            (id, ct) => RevokeUnderCertificateGateAsync(id, certificateId, revoke, ct), stopping, admission);

    private async Task<string> RevokeUnderCertificateGateAsync(Guid id, Guid certificateId,
        Func<X509Certificate2, string, CancellationToken, Task> revoke, CancellationToken ct)
    {
        var current = await _certificates.GetAsync(certificateId, ct);
        if (current is null) return "certificate.not_found";
        if (current.Kind != CertificateKind.Acme) return "certificate.self_signed_not_revocable";
        if (current.Status == CertificateStatus.Revoked) return "";
        if (string.IsNullOrWhiteSpace(current.ContactEmail)) return "certificate.contact_unavailable";
        if (_kestrel is null || _deployments is null) return "certificate.revocation_failed";
        using var material = await _certificates.LoadVersionAsync(certificateId, current.Version, ct);
        if (material is null) return "certificate.material_unavailable";
        var checkpoint = new RevocationCheckpoint(current.Version, material.GetCertHashString(HashAlgorithmName.SHA256));
        await UpdateRevocationAsync(id, checkpoint, "revocation-dispatched", "", ct);
        try
        {
            // The CA can commit even after the caller leaves. Drain the actual dispatch and retain its receipt.
            await revoke(material, current.ContactEmail, CancellationToken.None);
        }
        catch (Exception error)
        {
            _logger.LogWarning("CA revocation result is unverified. OperationId={OperationId}, FailureType={FailureType}", id, error.GetType().Name);
            await UpdateRevocationAsync(id, checkpoint, "revocation-recovery-required", RevocationRecoveryProblem);
            return RevocationRecoveryProblem;
        }

        checkpoint = checkpoint with { Confirmed = true };
        await UpdateRevocationAsync(id, checkpoint, "revocation-confirmed", "");
        if (await TryFinishRevocationAsync(certificateId, checkpoint, CancellationToken.None)) return "";
        await UpdateRevocationAsync(id, checkpoint, "revocation-cleanup-required", RevocationCleanupProblem);
        return RevocationCleanupProblem;
    }

    private async Task UpdateRevocationAsync(Guid id, RevocationCheckpoint checkpoint, string stage, string problem, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.Revocation is null && operation.Stage == "cancel-requested")
                throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();
            _operations[id] = operation with { Revocation = checkpoint, Stage = stage, ProblemCode = problem };
            try { await SaveLedgerAsync(CancellationToken.None); }
            catch (Exception error) { _operations[id] = operation; throw new ResidentWorkCompletionException(error); }
        }
        finally { _gate.Release(); }
    }

    private async Task<PersistedOperation> RecoverRevocationAsync(PersistedOperation operation, CancellationToken ct)
    {
        var checkpoint = operation.Revocation!;
        if (operation.Kind != "revoke" || operation.State != CertificateOperationState.Running
            || checkpoint.Version is not { Length: 32 } || !checkpoint.Version.All(Uri.IsHexDigit)
            || checkpoint.Sha256 is not { Length: 64 } || !checkpoint.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid certificate revocation checkpoint.");
        if (!checkpoint.Confirmed)
            return operation with { Stage = "revocation-recovery-required", ProblemCode = RevocationRecoveryProblem };
        return await TryFinishRevocationAsync(operation.CertificateId, checkpoint, ct)
            ? operation with { Revocation = null, State = CertificateOperationState.Succeeded, Stage = "succeeded", ProblemCode = "", CompletedAt = DateTimeOffset.UtcNow }
            : operation with { Stage = "revocation-cleanup-required", ProblemCode = RevocationCleanupProblem };
    }

    private async Task<bool> TryFinishRevocationAsync(Guid id, RevocationCheckpoint checkpoint, CancellationToken ct)
    {
        if (_kestrel is null || _deployments is null) return false;
        // A proven revoked leaf may never remain selectable, even if metadata cleanup is blocked.
        _kestrel.Deactivate(id, checkpoint.Sha256);
        try
        {
            var current = await _certificates.GetAsync(id, ct);
            if (current?.Version != checkpoint.Version) return false;
            using var material = await _certificates.LoadVersionAsync(id, checkpoint.Version, ct);
            if (material?.GetCertHashString(HashAlgorithmName.SHA256) != checkpoint.Sha256) return false;
            _kestrel.Deactivate(id);
            if (current.Status != CertificateStatus.Revoked)
                await _certificates.UpdateStatusAsync(id, CertificateStatus.Revoked, ct);
            await _deployments.RemoveKestrelAsync(id, ct);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or CertificateOperationException)
        {
            _logger.LogWarning("Confirmed revocation cleanup remains pending. CertificateId={CertificateId}, FailureType={FailureType}", id, error.GetType().Name);
            return false;
        }
    }

    private bool HasPendingRevocation(Guid certificateId, Guid except = default) =>
        _operations.Values.Any(operation => operation.CertificateId == certificateId && operation.OperationId != except
            && operation.Revocation is not null);

    private async Task<bool> IsRevocationBlockedAsync(Guid id, Guid certificateId)
    {
        await _gate.WaitAsync();
        try { return HasPendingRevocation(certificateId, id); }
        finally { _gate.Release(); }
    }

    private async Task<bool> HasRevocationCheckpointAsync(Guid id)
    {
        await _gate.WaitAsync();
        try { return _operations[id].Revocation is not null; }
        finally { _gate.Release(); }
    }

    private static async Task HoldPendingRevocationAsync(CancellationToken stopping)
    {
        // Keep ownership until orderly shutdown hands the already durable checkpoint to startup recovery.
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static CertificateOperationDto RevocationConflict(Guid certificateId, string kind) =>
        new(Guid.Empty, certificateId, kind, CertificateOperationState.Failed, "validation", RevocationRecoveryProblem, null, null);
}
