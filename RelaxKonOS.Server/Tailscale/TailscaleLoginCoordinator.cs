using System.Security.Claims;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

namespace RelaxKonOS.Server.Tailscale;

public interface ITailscaleLogin
{
    Task<TailscaleLoginOperation> PrepareAsync(ClaimsPrincipal principal, TailscaleLoginPrepareRequest request, CancellationToken ct);
    Task<TailscaleLoginOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct);
    Task<TailscaleLoginOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginChallengeResponse> ReadChallengeAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleLoginOperation> CloseAsync(ClaimsPrincipal principal, TailscaleLoginCloseRequest request, CancellationToken ct);
}

/// <summary>
/// Binds one first-login attempt to the authenticated account, login session and host installation.
/// The challenge URL is returned once and never enters the durable operation record.
/// </summary>
public sealed class TailscaleLoginCoordinator(TailscaleLoginJournal journal, ITailscaleLoginHost host,
    ITailscaleControlAuthorization grants, ServerRuntimeSettingsStore settings, TimeProvider? timeProvider = null) : ITailscaleLogin
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(5);
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public async Task<TailscaleLoginOperation> PrepareAsync(ClaimsPrincipal principal, TailscaleLoginPrepareRequest request, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        if (request.OperationId == Guid.Empty) throw new TailscaleControlException(400, "tailscale.invalid_request");
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        if (journal.Read(request.OperationId) is { } existing)
        {
            if (existing.Actor != actor || existing.SessionId != session) throw NotFound();
            return Normalize(existing).Operation;
        }
        var observation = await host.ObserveAsync(ct);
        if (!observation.CanStart || !Valid(observation))
            throw new TailscaleControlException(409, "tailscale.login.not_available");
        var now = Now;
        var operation = new TailscaleLoginOperation(request.OperationId, TailscaleLoginState.Prepared, observation.Revision,
            now.Add(PlanLifetime), now);
        journal.Save(new(actor, session, HostId(), operation, observation));
        return operation;
    }

    public async Task<TailscaleLoginOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(actor, session, operationId);
        if (stored.Operation.State != TailscaleLoginState.Prepared) return Normalize(stored).Operation;
        if (!confirmed) throw new TailscaleControlException(400, "tailscale.confirmation_required");
        if (stored.Operation.ExpiresAt <= Now) return SaveState(stored, TailscaleLoginState.Rejected, "tailscale.plan_expired").Operation;
        RequireGrant(principal);
        // Observe before writing Applying: a changed host or an already-running login never starts a native command here.
        var observation = await host.ObserveAsync(ct);
        if (!observation.CanStart || observation.Revision != stored.Operation.ExpectedRevision)
            return SaveState(stored, TailscaleLoginState.Rejected, "tailscale.login.state_changed").Operation;
        RequireGrant(principal);
        ct.ThrowIfCancellationRequested();
        if (stored.Operation.ExpiresAt <= Now)
            return SaveState(stored, TailscaleLoginState.Rejected, "tailscale.plan_expired").Operation;
        stored = SaveState(stored, TailscaleLoginState.Applying);
        TailscaleLoginReceipt receipt;
        try { receipt = await host.BeginAsync(new(Intent(stored), true), CancellationToken.None); }
        catch { return SaveState(stored, TailscaleLoginState.Unknown, "tailscale.login.outcome_unknown").Operation; }
        if (receipt.OperationId != operationId || receipt.State is not (TailscaleLoginState.Issued or TailscaleLoginState.Completed or TailscaleLoginState.ApprovalRequired))
            return SaveState(stored, TailscaleLoginState.Unknown, "tailscale.login.outcome_unknown").Operation;
        return SaveState(stored, receipt.State).Operation;
    }

    public async Task<TailscaleLoginOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileReceiptAsync(Normalize(Owned(actor, session, operationId, sameSession: false)), ct);
        return (await ReconcileAsync(stored, ct)).Operation;
    }

    public async Task<TailscaleLoginChallengeResponse> ReadChallengeAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = Normalize(Owned(actor, session, operationId));
        if (stored.Operation.State != TailscaleLoginState.Issued || stored.Operation.Closure is not null)
            throw new TailscaleControlException(409, "tailscale.login.challenge_unavailable");
        if (stored.Operation.ExpiresAt <= Now)
            throw new TailscaleControlException(409, "tailscale.plan_expired");
        var challenge = await host.ReadChallengeAsync(Intent(stored), ct);
        RequireGrant(principal);
        ct.ThrowIfCancellationRequested();
        if (stored.Operation.ExpiresAt <= Now)
            throw new TailscaleControlException(409, "tailscale.plan_expired");
        if (challenge.OperationId != operationId || !TailscaleLoginUrl.IsOfficial(challenge.AuthenticationUrl)
            || !Valid(challenge.Observation) || challenge.Observation.BackendState != "NeedsLogin"
            || challenge.Observation.PreferencesRevision != stored.Baseline.PreferencesRevision)
            throw new TailscaleControlException(409, "tailscale.login.challenge_unavailable");
        return new(operationId, challenge.AuthenticationUrl, stored.Operation.ExpiresAt);
    }

    public async Task<TailscaleLoginRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileAsync(Normalize(Owned(actor, session, operationId, sameSession: false)), ct);
        if (stored.Operation.Closure is { } closed) return new(operationId, closed.Observation, false, closed);
        if (stored.Operation.State is not (TailscaleLoginState.Unknown or TailscaleLoginState.Issued or TailscaleLoginState.ApprovalRequired))
            throw new TailscaleControlException(409, "tailscale.login.recovery.state_invalid");
        var observation = await host.ReadRecoveryAsync(Intent(stored), ct);
        RequireGrant(principal);
        if (!Valid(observation)) throw new TailscaleControlException(409, "tailscale.login.recovery.receipt_mismatch");
        var plan = new TailscaleLoginRecoveryPlan(observation.Revision, Now.Add(PlanLifetime));
        journal.Save(stored with { RecoveryPlan = plan });
        return new(operationId, observation, true, ExpiresAt: plan.ExpiresAt);
    }

    public async Task<TailscaleLoginOperation> CloseAsync(ClaimsPrincipal principal, TailscaleLoginCloseRequest request, CancellationToken ct)
    {
        var (actor, session) = Identity(principal);
        if (request.OperationId == Guid.Empty || !request.Confirmed || !Revision(request.ExpectedRevision))
            throw new TailscaleControlException(400, "tailscale.confirmation_required");
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileAsync(Normalize(Owned(actor, session, request.OperationId, sameSession: false)), ct);
        if (stored.Operation.Closure is { } existing)
        {
            if (existing.Observation.Revision != request.ExpectedRevision) throw new TailscaleControlException(409, "tailscale.revision_conflict");
            return stored.Operation;
        }
        if (stored.Operation.State is not (TailscaleLoginState.Unknown or TailscaleLoginState.Issued or TailscaleLoginState.ApprovalRequired))
            throw new TailscaleControlException(409, "tailscale.login.recovery.state_invalid");
        if (stored.RecoveryPlan is not { } plan || plan.ExpiresAt <= Now || plan.ExpectedRevision != request.ExpectedRevision)
            throw new TailscaleControlException(409, "tailscale.login.recovery.preview_required");
        var preview = await host.ReadRecoveryAsync(Intent(stored), ct);
        if (preview.Revision != request.ExpectedRevision)
            throw new TailscaleControlException(409, "tailscale.login.recovery.not_ready");
        RequireGrant(principal);
        ct.ThrowIfCancellationRequested();
        if (plan.ExpiresAt <= Now)
            throw new TailscaleControlException(409, "tailscale.login.recovery.preview_required");
        stored = stored with { ClosingRequest = request };
        journal.Save(stored);
        var closure = await host.CloseAsync(Intent(stored), request, CancellationToken.None);
        return AcceptClosure(stored, closure).Operation;
    }

    private async Task<StoredTailscaleLoginOperation> ReconcileAsync(StoredTailscaleLoginOperation stored, CancellationToken ct)
    {
        if (stored.Operation.Closure is not null || stored.ClosingRequest is null) return stored;
        var closure = await host.GetClosureAsync(Intent(stored), ct);
        return closure is null ? stored : AcceptClosure(stored, closure);
    }

    /// <summary>A Helper receipt recorded before a Server crash is accepted once; it is never replayed.</summary>
    private async Task<StoredTailscaleLoginOperation> ReconcileReceiptAsync(StoredTailscaleLoginOperation stored, CancellationToken ct)
    {
        if (stored.Operation.Closure is not null
            || stored.Operation.State is not (TailscaleLoginState.Unknown or TailscaleLoginState.Issued or TailscaleLoginState.ApprovalRequired))
            return stored;
        var receipt = await host.GetReceiptAsync(Intent(stored), ct);
        return receipt is not null && receipt.OperationId == stored.Operation.OperationId && receipt.RecordedAt != default
            && receipt.State is (TailscaleLoginState.Issued or TailscaleLoginState.Completed or TailscaleLoginState.ApprovalRequired)
            && receipt.State != stored.Operation.State ? SaveState(stored, receipt.State) : stored;
    }

    private StoredTailscaleLoginOperation AcceptClosure(StoredTailscaleLoginOperation stored, TailscaleLoginClosure closure)
    {
        if (closure.Intent != Intent(stored) || closure.ClosedAt == default
            || !Valid(closure.Observation) || stored.ClosingRequest?.ExpectedRevision != closure.Observation.Revision)
            throw new TailscaleControlException(409, "tailscale.login.recovery.receipt_mismatch");
        var updated = stored with
        {
            Operation = stored.Operation with { Closure = closure, UpdatedAt = closure.ClosedAt },
            RecoveryPlan = null,
            ClosingRequest = null,
        };
        journal.Save(updated);
        return updated;
    }

    private StoredTailscaleLoginOperation Normalize(StoredTailscaleLoginOperation stored)
    {
        if (stored.Operation.State == TailscaleLoginState.Applying)
            return SaveState(stored, TailscaleLoginState.Unknown, "tailscale.operation.interrupted");
        if (stored.Operation.State == TailscaleLoginState.Prepared && stored.Operation.ExpiresAt <= Now)
            return SaveState(stored, TailscaleLoginState.Rejected, "tailscale.plan_expired");
        return stored;
    }

    private StoredTailscaleLoginOperation SaveState(StoredTailscaleLoginOperation stored, TailscaleLoginState state, string? problem = null)
    {
        var updated = stored with
        {
            Operation = stored.Operation with { State = state, ProblemCode = problem, UpdatedAt = Now },
            RecoveryPlan = state is TailscaleLoginState.Rejected ? null : stored.RecoveryPlan,
        };
        journal.Save(updated);
        return updated;
    }

    // Only dispatch and challenge delivery require the original session. An owner may recover durable
    // evidence after reconnecting; recovery still demands a fresh grant and preserves the original intent.
    private StoredTailscaleLoginOperation Owned(string actor, Guid session, Guid id, bool sameSession = true) =>
        journal.Read(id) is { } stored && stored.Actor == actor && (!sameSession || stored.SessionId == session)
            ? stored : throw NotFound();

    private TailscaleLoginIntent Intent(StoredTailscaleLoginOperation stored) =>
        new(stored.Operation.OperationId, stored.HostId, Guid.Parse(stored.Actor), stored.SessionId,
            stored.Operation.ExpectedRevision, stored.Operation.ExpiresAt);

    private Guid HostId() => settings.Current.InstanceId is var id && id != Guid.Empty
        ? id : throw new TailscaleControlException(503, "tailscale.login.unavailable");

    private void RequireGrant(ClaimsPrincipal principal)
    {
        if (!grants.IsGranted(principal))
            throw new TailscaleControlException(403, "tailscale.elevation_required");
    }

    private static (string Actor, Guid SessionId) Identity(ClaimsPrincipal principal)
    {
        var actor = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var session = principal.FindFirst("sid")?.Value;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(actor, out var actorId) || actorId == Guid.Empty
            || !Guid.TryParse(session, out var sessionId) || sessionId == Guid.Empty)
            throw new TailscaleControlException(401, "tailscale.unauthenticated");
        return (actorId.ToString("D"), sessionId);
    }

    private static TailscaleControlException NotFound() => new(404, "tailscale.operation.not_found");
    private static bool Revision(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Valid(TailscaleLoginObservation observation) => Revision(observation.Revision)
        && Revision(observation.PreferencesRevision)
        && observation.BackendState is "NeedsLogin" or "NeedsMachineAuth" or "Running" or "Stopped";
}
