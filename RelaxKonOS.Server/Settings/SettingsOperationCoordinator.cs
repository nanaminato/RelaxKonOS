using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

/// <summary>Durable conditional plans; a canceled HTTP request cannot erase or replay an initiated host operation.</summary>
public sealed class SettingsOperationCoordinator(SettingsOperationJournal journal, IHostTimeService time,
    IHostElevationSessionStore elevations)
{
    public const string TimeResource = "host/time";
    private static readonly SettingsTarget TimeTarget = new(TimeResource, SettingsScope.HostMachine);

    public async Task<SettingsPlan> PreviewTimeAsync(ClaimsPrincipal principal, TimeZonePreviewRequest request, CancellationToken ct)
    {
        var actor = Actor(principal);
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        if (request.ExpectedRevision.Length != 64 || request.IdempotencyKey is not { Length: > 0 and <= 128 }
            || request.IdempotencyKey.Any(char.IsControl) || request.Change?.TimeZoneId is not { Length: > 0 and <= 256 })
            throw new SettingsException(400, "settings.invalid_request");
        var requestHash = SettingsRevisions.Hash(JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default));
        var id = new Guid(Convert.FromHexString(SettingsRevisions.Hash(actor + "\n" + TimeResource + "\n" + request.IdempotencyKey))[..16]);
        using var lease = await journal.AcquireAsync(ct);
        if (journal.Read(id) is { } existing)
        {
            if (existing.Actor != actor || existing.RequestHash != requestHash) throw new SettingsException(409, "settings.idempotency_conflict");
            return existing.Plan;
        }
        var snapshot = await time.ReadAsync(ct);
        if (snapshot.Revision != request.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        if (!snapshot.AvailableTimeZoneIds.Contains(request.Change.TimeZoneId, StringComparer.Ordinal))
            throw new SettingsException(400, "settings.time.invalid_zone");
        var plan = new SettingsPlan(id, TimeTarget, snapshot.Revision, DateTimeOffset.UtcNow.AddMinutes(5),
            [new("host.time.zone", snapshot.TimeZoneId, request.Change.TimeZoneId)], HostElevationCapability.HostTimeChange,
            TimeResource, SettingsEffectiveState.Immediate, "settings.time.affects_host_time_display");
        journal.Save(new StoredTimeOperation(actor, requestHash, request.Change, snapshot.TimeZoneId, plan,
            new(id, "host.time.zone", TimeTarget, SettingsOperationState.Prepared, DateTimeOffset.UtcNow)));
        return plan;
    }

    public async Task<SettingsOperation> ApplyTimeAsync(ClaimsPrincipal principal, Guid planId, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(principal, planId);
        if (stored.Operation.State == SettingsOperationState.Applying)
            return SaveState(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.operation.interrupted").Operation;
        if (stored.Operation.State != SettingsOperationState.Prepared) return stored.Operation;
        if (stored.Plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new SettingsException(428, "settings.plan_expired");
        if (!elevations.IsGranted(principal, HostElevationCapability.HostTimeChange, TimeResource))
            throw new SettingsException(428, "settings.elevation_required");
        var baseline = await time.ReadAsync(ct);
        if (baseline.Revision != stored.Plan.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        stored = SaveState(stored, SettingsOperationState.Applying);
        // Applying is committed with synchronous=FULL before crossing the privilege boundary.
        // The persisted operation id is reused; a restart sees Unknown, not a reason to send again.
        try
        {
            var result = await time.ApplyAsync(stored.Change, baseline.Revision, planId, CancellationToken.None);
            if (!result.Success)
                return SaveState(stored, result.ProblemCode is PrivilegedProblemCode.InvalidRequest or PrivilegedProblemCode.AccessDenied
                    or PrivilegedProblemCode.UnsupportedOperation ? SettingsOperationState.Failed : SettingsOperationState.Unknown,
                    "settings.helper." + result.ProblemCode.ToString().ToLowerInvariant()).Operation;
            var readback = await time.ReadAsync(CancellationToken.None);
            if (readback.TimeZoneId != stored.Change.TimeZoneId)
                return SaveState(stored, SettingsOperationState.Unknown, "settings.readback_mismatch", readback.Revision).Operation;
            return SaveState(stored, SettingsOperationState.Applied, revision: readback.Revision).Operation;
        }
        catch { return SaveState(stored, SettingsOperationState.Unknown, "settings.operation.outcome_unknown").Operation; }
    }

    public async Task<SettingsOperation> GetAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.Applying)
            stored = SaveState(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.operation.interrupted");
        return stored.Operation;
    }

    public async Task<SettingsOperation> RollbackAsync(ClaimsPrincipal principal, Guid id, SettingsRollbackRequest request, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.RolledBack) return stored.Operation;
        if (stored.Operation.State != SettingsOperationState.Applied) throw new SettingsException(409, "settings.rollback_state_invalid");
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        if (request.ExpectedRevision != stored.Operation.ObservedRevision) throw new SettingsException(409, "settings.revision_conflict");
        if (!elevations.IsGranted(principal, HostElevationCapability.HostTimeChange, TimeResource))
            throw new SettingsException(428, "settings.elevation_required");
        var baseline = await time.ReadAsync(ct);
        if (baseline.Revision != stored.Operation.ObservedRevision) throw new SettingsException(409, "settings.revision_conflict");
        stored = SaveState(stored with { RollingBack = true }, SettingsOperationState.Applying);
        var rollbackId = new Guid(Convert.FromHexString(SettingsRevisions.Hash(id.ToString("D") + "rollback"))[..16]);
        try
        {
            var result = await time.ApplyAsync(new(stored.OriginalTimeZone), baseline.Revision, rollbackId, CancellationToken.None);
            if (!result.Success) return SaveState(stored, SettingsOperationState.RecoveryRequired, "settings.rollback.outcome_unknown").Operation;
            var observed = await time.ReadAsync(CancellationToken.None);
            return SaveState(stored, observed.TimeZoneId == stored.OriginalTimeZone ? SettingsOperationState.RolledBack : SettingsOperationState.RecoveryRequired,
                revision: observed.Revision).Operation;
        }
        catch { return SaveState(stored, SettingsOperationState.RecoveryRequired, "settings.rollback.outcome_unknown").Operation; }
    }

    private StoredTimeOperation Owned(ClaimsPrincipal principal, Guid id)
    {
        var actor = Actor(principal);
        var stored = journal.Read(id);
        if (stored is null || stored.Actor != actor) throw new SettingsException(404, "settings.operation_not_found");
        return stored;
    }

    private StoredTimeOperation SaveState(StoredTimeOperation stored, SettingsOperationState state, string? problem = null, string? revision = null)
    {
        var updated = stored with { Operation = stored.Operation with
            { State = state, UpdatedAt = DateTimeOffset.UtcNow, ProblemCode = problem, ObservedRevision = revision } };
        journal.Save(updated);
        return updated;
    }

    private static string Actor(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return principal.Identity?.IsAuthenticated == true && Guid.TryParse(value, out var id)
            ? id.ToString("D") : throw new SettingsException(401, "settings.unauthenticated");
    }
}
