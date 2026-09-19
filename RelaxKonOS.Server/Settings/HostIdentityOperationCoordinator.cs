using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

/// <summary>
/// Durable conditional plans for the remote host name. The platform read decides the real state; the
/// journal only records the operation, so a lost HTTP response can never be mistaken for a write.
/// </summary>
public sealed class HostIdentityOperationCoordinator(SettingsOperationJournal journal, IHostIdentityService identity,
    IHostElevationSessionStore elevations)
{
    public const string IdentityResource = "host/identity";
    private static readonly SettingsTarget IdentityTarget = new(IdentityResource, SettingsScope.HostMachine);

    public async Task<SettingsPlan> PreviewAsync(ClaimsPrincipal principal, HostnamePreviewRequest request, CancellationToken ct)
    {
        var actor = Actor(principal);
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        if (request.ExpectedRevision.Length != 64 || request.IdempotencyKey is not { Length: > 0 and <= 128 }
            || request.IdempotencyKey.Any(char.IsControl)) throw new SettingsException(400, "settings.invalid_request");
        // Shared syntax rules run against the local platform; the provider's own maximum is enforced below.
        if (HostIdentityValidation.Validate(request.Change, OperatingSystem.IsWindows()) is { } problem)
            throw new SettingsException(400, problem);
        var requestHash = SettingsRevisions.Hash(JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default));
        var id = new Guid(Convert.FromHexString(SettingsRevisions.Hash(actor + "\n" + IdentityResource + "\n" + request.IdempotencyKey))[..16]);
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadIdentity(id) is { } existing)
        {
            if (existing.Actor != actor || existing.RequestHash != requestHash) throw new SettingsException(409, "settings.idempotency_conflict");
            return existing.Plan;
        }
        var snapshot = await identity.ReadAsync(ct);
        if (snapshot.Revision != request.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        if (request.Change.HostName.Length > snapshot.MaximumHostNameLength) throw new SettingsException(400, "settings.identity.invalid_name");
        if (string.Equals(snapshot.PendingHostName, request.Change.HostName, StringComparison.Ordinal))
            throw new SettingsException(400, "settings.identity.unchanged");
        var effectiveState = EffectiveState();
        var plan = new SettingsPlan(id, IdentityTarget, snapshot.Revision, DateTimeOffset.UtcNow.AddMinutes(5),
            [new("host.identity.hostname", snapshot.PendingHostName, request.Change.HostName)],
            HostElevationCapability.HostIdentityChange, IdentityResource, effectiveState,
            effectiveState == SettingsEffectiveState.HostRestart
                ? "settings.identity.restart_required" : "settings.identity.applies_immediately");
        journal.Save(new StoredIdentityOperation(actor, requestHash, request.Change, snapshot.PendingHostName, plan,
            new(id, "host.identity.hostname", IdentityTarget, SettingsOperationState.Prepared, DateTimeOffset.UtcNow,
                EffectiveState: effectiveState)));
        return plan;
    }

    public async Task<SettingsOperation> ApplyAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.Applying) return Interrupted(stored).Operation;
        if (stored.Operation.State != SettingsOperationState.Prepared) return stored.Operation;
        if (stored.Plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new SettingsException(428, "settings.plan_expired");
        RequireGrant(principal);
        var baseline = await identity.ReadAsync(ct);
        if (baseline.Revision != stored.Plan.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        return await ExecuteAsync(stored, stored.Change, baseline.Revision, id);
    }

    public async Task<SettingsOperation?> GetIfExistsAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadIdentity(id) is null) return null;
        var stored = Owned(principal, id);
        return stored.Operation.State == SettingsOperationState.Applying ? Interrupted(stored).Operation : stored.Operation;
    }

    public async Task<SettingsOperation?> RollbackIfExistsAsync(ClaimsPrincipal principal, Guid id,
        SettingsRollbackRequest request, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadIdentity(id) is null) return null;
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.RolledBack) return stored.Operation;
        if (stored.Operation.State != SettingsOperationState.Applied) throw new SettingsException(409, "settings.rollback_state_invalid");
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        RequireGrant(principal);
        var baseline = await identity.ReadAsync(ct);
        // A newer edit by another administrator must not be overwritten by this rollback.
        if (baseline.Revision != request.ExpectedRevision || baseline.Revision != stored.Operation.ObservedRevision)
            throw new SettingsException(409, "settings.revision_conflict");
        var rollbackId = new Guid(Convert.FromHexString(SettingsRevisions.Hash(id.ToString("D") + "rollback"))[..16]);
        return await ExecuteAsync(stored with { RollingBack = true }, new(stored.OriginalHostName), baseline.Revision, rollbackId);
    }

    private async Task<SettingsOperation> ExecuteAsync(StoredIdentityOperation stored, HostnameChange change,
        string revision, Guid helperId)
    {
        stored = Save(stored, SettingsOperationState.Applying);
        // synchronous=FULL encrypted commit precedes IPC. No cancellation of an in-flight host mutation.
        try
        {
            var result = await identity.ApplyAsync(change, revision, helperId, CancellationToken.None);
            if (!result.Success || result.HostIdentity is not { } observed)
                return Save(stored, Failure(stored, result.ProblemCode),
                    "settings.helper." + result.ProblemCode.ToString().ToLowerInvariant()).Operation;
            // Windows confirms through the staged name; Linux reports the same value for both fields.
            if (!string.Equals(observed.PendingHostName, change.HostName, StringComparison.Ordinal))
                return Save(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown,
                    "settings.readback_mismatch", observed.Revision).Operation;
            return Save(stored, stored.RollingBack ? SettingsOperationState.RolledBack : SettingsOperationState.Applied,
                revision: observed.Revision).Operation;
        }
        catch { return Save(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown,
            "settings.operation.outcome_unknown").Operation; }
    }

    /// <summary>A reported refusal is a known failure; anything else leaves the real host state unknown.</summary>
    private static SettingsOperationState Failure(StoredIdentityOperation stored, PrivilegedProblemCode problemCode) =>
        problemCode is PrivilegedProblemCode.InvalidRequest or PrivilegedProblemCode.AccessDenied
            or PrivilegedProblemCode.UnsupportedOperation or PrivilegedProblemCode.ResourceNotAllowed
            ? SettingsOperationState.Failed
            : stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown;

    private static SettingsEffectiveState EffectiveState() =>
        OperatingSystem.IsWindows() ? SettingsEffectiveState.HostRestart : SettingsEffectiveState.Immediate;

    private StoredIdentityOperation Owned(ClaimsPrincipal principal, Guid id)
    {
        var stored = journal.ReadIdentity(id);
        if (stored is null || stored.Actor != Actor(principal)) throw new SettingsException(404, "settings.operation_not_found");
        return stored;
    }

    private void RequireGrant(ClaimsPrincipal principal)
    {
        if (!elevations.IsGranted(principal, HostElevationCapability.HostIdentityChange, IdentityResource))
            throw new SettingsException(428, "settings.elevation_required");
    }

    private StoredIdentityOperation Interrupted(StoredIdentityOperation stored) => Save(stored,
        stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.operation.interrupted");

    private StoredIdentityOperation Save(StoredIdentityOperation stored, SettingsOperationState state, string? problem = null, string? revision = null)
    {
        var updated = stored with { Operation = stored.Operation with
            { State = state, ProblemCode = problem, ObservedRevision = revision, UpdatedAt = DateTimeOffset.UtcNow } };
        journal.Save(updated);
        return updated;
    }

    private static string Actor(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return principal.Identity?.IsAuthenticated == true && Guid.TryParse(subject, out var id) ? id.ToString("D")
            : throw new SettingsException(401, "settings.unauthenticated");
    }
}
