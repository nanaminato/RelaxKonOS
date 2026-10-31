using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Tailscale;

public interface ITailscaleControlHost
{
    Task<TailscaleControlSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<TailscaleControlResult> ExecuteAsync(TailscalePrepareRequest request, CancellationToken cancellationToken);
    Task<TailscaleControlResult?> GetResultAsync(Guid operationId, CancellationToken cancellationToken);
    Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(Guid operationId, CancellationToken cancellationToken);
    Task<TailscaleRecoveryClosure> CloseAsync(TailscaleCloseRequest request, string actor, CancellationToken cancellationToken);
    Task<TailscaleRecoveryClosure?> GetClosureAsync(Guid operationId, CancellationToken cancellationToken);
}

public interface ITailscaleControl
{
    Task<TailscaleControlSnapshot> ReadAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<TailscaleOperation> PrepareAsync(ClaimsPrincipal principal, TailscalePrepareRequest request, CancellationToken ct);
    Task<TailscaleOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct);
    Task<TailscaleOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct);
    Task<TailscaleOperation> CloseAsync(ClaimsPrincipal principal, TailscaleCloseRequest request, CancellationToken ct);
}

public sealed class TailscaleControlException(int statusCode, string problemCode) : Exception(problemCode)
{
    public int StatusCode { get; } = statusCode;
    public string ProblemCode { get; } = problemCode;
}

/// <summary>Serializes every mutation and journals intent before IPC. Interrupted writes are never replayed.</summary>
public sealed class TailscaleOperationCoordinator(TailscaleOperationJournal journal, ITailscaleControlHost host,
    ITailscaleControlAuthorization grants, TimeProvider? timeProvider = null) : ITailscaleControl
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    // Viewing the connection snapshot is an observation, not a host change: any authenticated
    // account may read whether the daemon is connected and which preferences it uses. Peer
    // identities stay restricted by the status route, and every mutation still requires a
    // fresh HostNetworkChange/host/tailscale grant below.
    public Task<TailscaleControlSnapshot> ReadAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        Actor(principal);
        return host.ReadAsync(ct);
    }

    public async Task<TailscaleOperation> PrepareAsync(ClaimsPrincipal principal, TailscalePrepareRequest request, CancellationToken ct)
    {
        var actor = Actor(principal);
        if (request.OperationId == Guid.Empty || !Revision(request.ExpectedRevision) || !Enum.IsDefined(request.Action)
            || (request.Action == TailscaleAction.Configure) != (request.Configuration is not null)
            || (request.Action == TailscaleAction.ConfigurePreferences) != (request.Preferences is not null)
            || request.Configuration is not null && request.Action != TailscaleAction.Configure
            || request.Preferences is { } requested && !ValidPreferences(requested))
            throw new TailscaleControlException(400, "tailscale.invalid_request");
        request = request.Preferences is { } preferences
            ? request with { Preferences = preferences with { ExitNode = NormalizeExitNode(preferences.ExitNode) } }
            : request;
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        if (journal.Read(request.OperationId) is { } existing)
        {
            if (existing.Actor != actor || existing.Operation.Action != request.Action
                || existing.Operation.ExpectedRevision != request.ExpectedRevision || existing.Operation.Configuration != request.Configuration
                || existing.Operation.Preferences != request.Preferences)
                throw new TailscaleControlException(409, "tailscale.idempotency_conflict");
            return Normalize(existing).Operation;
        }
        var snapshot = await host.ReadAsync(ct);
        ValidateBaseline(snapshot, request);
        var now = Now;
        var operation = new TailscaleOperation(request.OperationId, request.Action, TailscaleOperationState.Prepared,
            request.ExpectedRevision, now.AddMinutes(5), now, request.Configuration, Preferences: request.Preferences);
        journal.Save(new(actor, operation, snapshot));
        return operation;
    }

    public async Task<TailscaleOperation> ExecuteAsync(ClaimsPrincipal principal, Guid operationId, bool confirmed, CancellationToken ct)
    {
        var actor = Actor(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = Owned(actor, operationId);
        if (stored.Operation.State != TailscaleOperationState.Prepared) return Normalize(stored).Operation;
        if (!confirmed) throw new TailscaleControlException(400, "tailscale.confirmation_required");
        if (stored.Operation.ExpiresAt <= Now) return Save(stored, TailscaleOperationState.Rejected, "tailscale.plan_expired");
        RequireGrant(principal);
        var operation = stored.Operation;
        var request = new TailscalePrepareRequest(operationId, operation.ExpectedRevision, operation.Action, operation.Configuration, operation.Preferences);
        ValidateBaseline(await host.ReadAsync(ct), request);
        // A prepared plan grants no authority: re-check after waiting for the lock and baseline read.
        RequireGrant(principal);
        ct.ThrowIfCancellationRequested();
        if (stored.Operation.ExpiresAt <= Now) return Save(stored, TailscaleOperationState.Rejected, "tailscale.plan_expired");
        Save(stored, TailscaleOperationState.Applying);
        try
        {
            // Submission becomes independent of HTTP cancellation only after the durable Applying record.
            var result = await host.ExecuteAsync(request, CancellationToken.None);
            if (result.OperationId != operationId || !result.Completed || result.Snapshot is not { } observed
                || !Revision(observed.Revision) || !Matches(request, stored.Baseline, observed))
                return Save(stored, TailscaleOperationState.Unknown, "tailscale.operation.outcome_unknown");
            return Save(stored, TailscaleOperationState.Applied, revision: observed.Revision);
        }
        catch { return Save(stored, TailscaleOperationState.Unknown, "tailscale.operation.outcome_unknown"); }
    }

    public async Task<TailscaleOperation> GetAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct)
    {
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileClosureAsync(Owned(Actor(principal), operationId), ct);
        if (stored.Operation.Closure is not null) return stored.Operation;
        if (stored.Operation.State is TailscaleOperationState.Applying or TailscaleOperationState.Unknown)
        {
            var receipt = await host.GetResultAsync(operationId, ct);
            var operation = stored.Operation;
            var request = new TailscalePrepareRequest(operationId, operation.ExpectedRevision, operation.Action, operation.Configuration, operation.Preferences);
            if (receipt?.OperationId == operationId && receipt.Completed && receipt.Snapshot is { } observed
                && Revision(observed.Revision) && Matches(request, stored.Baseline, observed))
                return Save(stored, TailscaleOperationState.Applied, revision: observed.Revision);
        }
        return Normalize(stored).Operation;
    }

    public async Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(ClaimsPrincipal principal, Guid operationId, CancellationToken ct)
    {
        var actor = Actor(principal);
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileClosureAsync(Normalize(Owned(actor, operationId)), ct);
        if (stored.Operation.Closure is { } closed) return new(operationId, closed.Observation, false, closed);
        if (stored.Operation.State != TailscaleOperationState.Unknown) throw new TailscaleControlException(409, "tailscale.recovery.state_invalid");
        var recovery = await host.ReadRecoveryAsync(operationId, ct);
        RequireGrant(principal);
        if (recovery.OperationId != operationId || recovery.Closure is not null)
            throw new TailscaleControlException(409, "tailscale.recovery.receipt_mismatch");
        var plan = recovery.CanClose && recovery.Observation is { } observation && Revision(observation.Revision)
            && (stored.Operation.Action == TailscaleAction.Logout || observation.CanControl)
            ? new TailscaleRecoveryPlan(observation.Revision, Now.AddMinutes(5)) : null;
        journal.Save(stored with { RecoveryPlan = plan });
        return recovery with { CanClose = plan is not null, ExpiresAt = plan?.ExpiresAt };
    }

    public async Task<TailscaleOperation> CloseAsync(ClaimsPrincipal principal, TailscaleCloseRequest request, CancellationToken ct)
    {
        var actor = Actor(principal);
        if (request.OperationId == Guid.Empty || !request.Confirmed || !Revision(request.ExpectedRevision))
            throw new TailscaleControlException(400, "tailscale.confirmation_required");
        RequireGrant(principal);
        using var lease = await journal.AcquireAsync(ct);
        var stored = await ReconcileClosureAsync(Normalize(Owned(actor, request.OperationId)), ct);
        if (stored.Operation.Closure is { } existing)
        {
            if (existing.Observation.Revision != request.ExpectedRevision) throw new TailscaleControlException(409, "tailscale.revision_conflict");
            return stored.Operation;
        }
        if (stored.Operation.State != TailscaleOperationState.Unknown || stored.RecoveryPlan is not { } plan
            || plan.ExpiresAt <= Now || plan.ExpectedRevision != request.ExpectedRevision)
            throw new TailscaleControlException(409, "tailscale.recovery.preview_required");
        var preview = await host.ReadRecoveryAsync(request.OperationId, ct);
        if (preview.OperationId != request.OperationId || !preview.CanClose || preview.Closure is not null
            || preview.Observation is not { } closeObservation
            || (!closeObservation.CanControl && stored.Operation.Action != TailscaleAction.Logout)
            || closeObservation.Revision != request.ExpectedRevision)
            throw new TailscaleControlException(409, "tailscale.recovery.not_ready");
        RequireGrant(principal);
        ct.ThrowIfCancellationRequested();
        if (plan.ExpiresAt <= Now) throw new TailscaleControlException(409, "tailscale.recovery.preview_required");
        stored = stored with { ClosingRequest = request };
        journal.Save(stored);
        var closure = await host.CloseAsync(request, actor, CancellationToken.None);
        return AcceptClosure(stored, closure).Operation;
    }

    private async Task<StoredTailscaleOperation> ReconcileClosureAsync(StoredTailscaleOperation stored, CancellationToken ct)
    {
        if (stored.Operation.Closure is not null || stored.ClosingRequest is null) return stored;
        var closure = await host.GetClosureAsync(stored.Operation.OperationId, ct);
        return closure is null ? stored : AcceptClosure(stored, closure);
    }
    private StoredTailscaleOperation AcceptClosure(StoredTailscaleOperation stored, TailscaleRecoveryClosure closure)
    {
        if (closure.OperationId != stored.Operation.OperationId || closure.Actor != stored.Actor
            || closure.ClosedAt == default || closure.Observation is null || closure.Observation.Configuration is null
            || !closure.Observation.CanControl && stored.Operation.Action != TailscaleAction.Logout
            || stored.ClosingRequest?.ExpectedRevision != closure.Observation.Revision || !Revision(closure.Observation.Revision)
            || stored.Operation.State != TailscaleOperationState.Unknown)
            throw new TailscaleControlException(409, "tailscale.recovery.receipt_mismatch");
        var closed = stored with { Operation = stored.Operation with { Closure = closure, UpdatedAt = closure.ClosedAt }, RecoveryPlan = null };
        journal.Save(closed);
        return closed;
    }

    private StoredTailscaleOperation Normalize(StoredTailscaleOperation stored)
    {
        if (stored.Operation.State == TailscaleOperationState.Applying)
            return stored with { Operation = Save(stored, TailscaleOperationState.Unknown, "tailscale.operation.interrupted") };
        if (stored.Operation.State == TailscaleOperationState.Prepared && stored.Operation.ExpiresAt <= Now)
            return stored with { Operation = Save(stored, TailscaleOperationState.Rejected, "tailscale.plan_expired") };
        return stored;
    }

    private StoredTailscaleOperation Owned(string actor, Guid id)
    {
        var stored = journal.Read(id);
        return stored is not null && stored.Actor == actor ? stored : throw new TailscaleControlException(404, "tailscale.operation.not_found");
    }

    private TailscaleOperation Save(StoredTailscaleOperation stored, TailscaleOperationState state, string? problem = null, string? revision = null)
    {
        var operation = stored.Operation with { State = state, ProblemCode = problem, ObservedRevision = revision, UpdatedAt = Now };
        journal.Save(stored with { Operation = operation });
        return operation;
    }

    private static void ValidateBaseline(TailscaleControlSnapshot snapshot, TailscalePrepareRequest request)
    {
        if (!snapshot.CanControl || !Revision(snapshot.Revision)) throw new TailscaleControlException(409, "tailscale.control_unavailable");
        if (snapshot.Revision != request.ExpectedRevision) throw new TailscaleControlException(409, "tailscale.revision_conflict");
        if (request.Action == TailscaleAction.Connect && !snapshot.Authenticated) throw new TailscaleControlException(409, "tailscale.login_required");
        if (request.Action == TailscaleAction.ConfigurePreferences && snapshot.Preferences is null)
            throw new TailscaleControlException(409, "tailscale.control_unavailable");
        if (request.Action == TailscaleAction.Logout && !snapshot.Authenticated)
            throw new TailscaleControlException(409, "tailscale.control_unavailable");
    }

    private static bool Matches(TailscalePrepareRequest request, TailscaleControlSnapshot baseline, TailscaleControlSnapshot snapshot) => request.Action switch
    {
        TailscaleAction.Connect => snapshot.Connected && snapshot.Authenticated && snapshot.Configuration == baseline.Configuration,
        TailscaleAction.Disconnect => !snapshot.Connected && snapshot.Configuration == baseline.Configuration,
        TailscaleAction.Configure => snapshot.Configuration == request.Configuration && snapshot.Connected == baseline.Connected,
        TailscaleAction.ConfigurePreferences => snapshot.Preferences == request.Preferences
            && snapshot.Connected == baseline.Connected && snapshot.Authenticated,
        TailscaleAction.Logout => !snapshot.Authenticated && !snapshot.Connected,
        _ => false
    };
    private static bool ValidPreferences(TailscalePreferences preferences) => preferences.ExitNode is null or "" or "auto:any"
        || System.Net.IPAddress.TryParse(preferences.ExitNode, out _);
    private static string? NormalizeExitNode(string? value) => value == "auto:any" ? "auto:any"
        : value is { Length: > 0 } && System.Net.IPAddress.TryParse(value, out var address) ? address.ToString() : null;
    private void RequireGrant(ClaimsPrincipal principal)
    {
        if (!grants.IsGranted(principal))
            throw new TailscaleControlException(403, "tailscale.elevation_required");
    }
    private static bool Revision(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Actor(ClaimsPrincipal principal)
    {
        var actor = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return principal.Identity?.IsAuthenticated == true && Guid.TryParse(actor, out var id) && id != Guid.Empty ? id.ToString("D")
            : throw new TailscaleControlException(401, "tailscale.unauthenticated");
    }
}
