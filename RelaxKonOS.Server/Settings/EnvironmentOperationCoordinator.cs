using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Server.Settings;

public sealed class EnvironmentOperationCoordinator(SettingsOperationJournal journal, IHostEnvironmentService environment)
{
    public async Task<SettingsPlan> PreviewAsync(ClaimsPrincipal principal, EnvironmentPreviewRequest request, CancellationToken ct)
    {
        var target = environment.ResolveTarget(principal, request.Scope);
        environment.RequireGrant(principal, target, HostElevationCapability.HostEnvironmentRead);
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        if (request.ExpectedRevision.Length != 64 || request.IdempotencyKey is not { Length: > 0 and <= 128 }
            || request.IdempotencyKey.Any(char.IsControl)) throw new SettingsException(400, "settings.invalid_request");
        if (EnvironmentValidation.Validate(request.Change, OperatingSystem.IsWindows()) is { } problem) throw new SettingsException(400, problem);
        var actor = Actor(principal);
        var hash = SettingsRevisions.Hash(JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default));
        var id = new Guid(Convert.FromHexString(SettingsRevisions.Hash(actor + "\n" + target.ResourceId + "\n" + request.IdempotencyKey))[..16]);
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadEnvironment(id) is { } previous)
        {
            if (previous.Actor != actor || previous.RequestHash != hash) throw new SettingsException(409, "settings.idempotency_conflict");
            return previous.Plan;
        }
        var baseline = await environment.ReadRawAsync(principal, target, ct);
        if (baseline.Revision != request.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        var values = baseline.Values.ToDictionary(value => value.Name, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var restore = request.Change.Changes.Select(change => values.TryGetValue(change.Name, out var original)
            ? new EnvironmentMutation(original.Name, EnvironmentMutationKind.Set, original.Value, original.Kind)
            : new EnvironmentMutation(change.Name, EnvironmentMutationKind.Delete)).ToArray();
        // Plan output never serializes an environment value, including secrets under an unexpected name.
        var differences = request.Change.Changes.Select(change => new SettingsDifference(change.Name,
            values.ContainsKey(change.Name) ? "[configured]" : null, change.Operation == EnvironmentMutationKind.Delete ? null : "[configured]")).ToArray();
        var plan = new SettingsPlan(id, target, baseline.Revision, DateTimeOffset.UtcNow.AddMinutes(5), differences,
            HostElevationCapability.HostEnvironmentChange, target.ResourceId, SettingsEffectiveState.NewProcess,
            "settings.environment.new_process_required");
        journal.Save(new StoredEnvironmentOperation(actor, hash, request.Change, new(restore, ConfirmHighImpact: true), plan,
            new(id, "host.environment", target, SettingsOperationState.Prepared, DateTimeOffset.UtcNow, EffectiveState: SettingsEffectiveState.NewProcess)));
        return plan;
    }

    public async Task<SettingsOperation> ApplyAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.Applying) return Interrupted(stored).Operation;
        if (stored.Operation.State != SettingsOperationState.Prepared) return stored.Operation;
        if (stored.Plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new SettingsException(428, "settings.plan_expired");
        environment.RequireGrant(principal, stored.Plan.Target, HostElevationCapability.HostEnvironmentChange);
        var baseline = await environment.ReadRawAsync(principal, stored.Plan.Target, ct);
        if (baseline.Revision != stored.Plan.ExpectedRevision) throw new SettingsException(409, "settings.revision_conflict");
        return await ExecuteAsync(principal, stored, stored.Change, baseline.Revision, id);
    }

    public async Task<SettingsOperation?> GetIfExistsAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadEnvironment(id) is null) return null;
        var stored = Owned(principal, id);
        return stored.Operation.State == SettingsOperationState.Applying ? Interrupted(stored).Operation : stored.Operation;
    }

    public async Task<SettingsOperation?> RollbackIfExistsAsync(ClaimsPrincipal principal, Guid id, SettingsRollbackRequest request, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        if (journal.ReadEnvironment(id) is null) return null;
        var stored = Owned(principal, id);
        if (stored.Operation.State == SettingsOperationState.RolledBack) return stored.Operation;
        if (stored.Operation.State != SettingsOperationState.Applied) throw new SettingsException(409, "settings.rollback_state_invalid");
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        environment.RequireGrant(principal, stored.Plan.Target, HostElevationCapability.HostEnvironmentChange);
        var baseline = await environment.ReadRawAsync(principal, stored.Plan.Target, ct);
        if (baseline.Revision != request.ExpectedRevision || baseline.Revision != stored.Operation.ObservedRevision)
            throw new SettingsException(409, "settings.revision_conflict");
        var rollbackId = new Guid(Convert.FromHexString(SettingsRevisions.Hash(id.ToString("D") + "rollback"))[..16]);
        return await ExecuteAsync(principal, stored with { RollingBack = true }, stored.Restore, baseline.Revision, rollbackId);
    }

    private async Task<SettingsOperation> ExecuteAsync(ClaimsPrincipal principal, StoredEnvironmentOperation stored,
        EnvironmentChangeSet change, string revision, Guid helperId)
    {
        stored = Save(stored, SettingsOperationState.Applying);
        // synchronous=FULL encrypted commit precedes IPC. No cancellation of an in-flight host mutation.
        try
        {
            var result = await environment.ApplyAsync(principal, stored.Plan.Target, change, revision, helperId, CancellationToken.None);
            if (!result.Success || result.HostEnvironment is not { } observed)
                return Save(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown,
                    "settings.environment.helper_" + result.ProblemCode.ToString().ToLowerInvariant()).Operation;
            if (observed.Target != stored.Plan.Target || !Matches(observed, change))
                return Save(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.readback_mismatch").Operation;
            return Save(stored, stored.RollingBack ? SettingsOperationState.RolledBack : SettingsOperationState.Applied,
                observed.NotificationDelivered ? null : "settings.environment.notification_not_delivered", observed.Revision).Operation;
        }
        catch { return Save(stored, stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.operation.outcome_unknown").Operation; }
    }
    private static bool Matches(PrivilegedEnvironmentState state, EnvironmentChangeSet change)
    {
        var values = state.Values.ToDictionary(value => value.Name, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return change.Changes.All(change => change.Operation == EnvironmentMutationKind.Delete ? !values.ContainsKey(change.Name)
            : values.TryGetValue(change.Name, out var value) && value.Value == change.Value && value.Kind == change.ValueKind);
    }
    private StoredEnvironmentOperation Owned(ClaimsPrincipal principal, Guid id)
    {
        var stored = journal.ReadEnvironment(id);
        if (stored is null || stored.Actor != Actor(principal)) throw new SettingsException(404, "settings.operation_not_found");
        if (environment.ResolveTarget(principal, stored.Plan.Target.Scope) != stored.Plan.Target) throw new SettingsException(403, "settings.environment.identity_mismatch");
        return stored;
    }
    private StoredEnvironmentOperation Interrupted(StoredEnvironmentOperation stored) => Save(stored,
        stored.RollingBack ? SettingsOperationState.RecoveryRequired : SettingsOperationState.Unknown, "settings.operation.interrupted");
    private StoredEnvironmentOperation Save(StoredEnvironmentOperation stored, SettingsOperationState state, string? problem = null, string? revision = null)
    {
        var updated = stored with { Operation = stored.Operation with { State = state, ProblemCode = problem, ObservedRevision = revision, UpdatedAt = DateTimeOffset.UtcNow } };
        journal.Save(updated); return updated;
    }
    private static string Actor(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return principal.Identity?.IsAuthenticated == true && Guid.TryParse(subject, out var id) ? id.ToString("D")
            : throw new SettingsException(401, "settings.unauthenticated");
    }
}
