using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Server-owned evidence from a live identity/policy verifier; never an HTTP request model.</summary>
public sealed record TaskApprovalIdentity(string CanonicalSubject, string SessionIdentity, string HostId,
    string PolicyRevision, string ApproverIdentity, string AuthenticationMethod, DateTimeOffset ValidUntil);
/// <summary>Current identity observation; possession of a stored approval does not produce this evidence.</summary>
public sealed record TaskExecutionIdentity(string CanonicalSubject, string SessionIdentity, string HostId, string PolicyRevision);

public sealed partial class TaskPlanStore
{
    /// <summary>Internal durable grant only. No generic elevation store or Helper is called.</summary>
    public TaskPlanApproval RecordApproval(Guid planId, string digest, TaskApprovalIdentity identity, string key) => Guard(() =>
    {
        ValidateIdentity(identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.PolicyRevision);
        if (!ValidSubject(identity.ApproverIdentity) || !ValidSubject(identity.AuthenticationMethod))
            throw new TaskPlanStoreException("task.approval_identity_invalid", 403);
        if (identity.ValidUntil <= _clock.GetUtcNow()) throw new TaskPlanStoreException("task.approval_unavailable", 403);
        var keyReference = ExecutionKey(identity.CanonicalSubject, key);
        var requestReference = Fingerprint(new { planId, digest, identity.CanonicalSubject, identity.SessionIdentity,
            identity.HostId, identity.PolicyRevision, identity.ApproverIdentity, identity.AuthenticationMethod });
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var previous = Command(connection, transaction,
            "SELECT approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record FROM task_approvals WHERE idempotency_reference=$key;", ("$key", keyReference)))
        using (var reader = previous.ExecuteReader())
        {
            if (reader.Read())
            {
                var saved = DecodeApproval(reader);
                if (saved.RequestReference != requestReference) throw new TaskPlanStoreException("task.idempotency_conflict", 409);
                return saved.Record;
            }
        }
        var plan = RequireCurrentPlan(connection, transaction, planId, identity.CanonicalSubject, digest);
        if (plan.Plan.Semantics.HostId != identity.HostId) throw new TaskPlanStoreException("task.approval_identity_invalid", 403);
        var capabilities = ProposalCapabilities(plan.Plan);
        using (var runs = Command(connection, transaction, "SELECT COUNT(*) FROM task_runs WHERE plan_id=$plan;", ("$plan", planId.ToString("D"))))
            if ((long)runs.ExecuteScalar()! != 0) throw new TaskPlanStoreException("task.plan_already_started", 409);
        using (var quota = Command(connection, transaction, "SELECT COUNT(*) FROM task_approvals WHERE plan_id=$plan;", ("$plan", planId.ToString("D"))))
            if ((long)quota.ExecuteScalar()! >= 16) throw new TaskPlanStoreException("task.approval_capacity_exceeded", 409);
        var now = _clock.GetUtcNow();
        var startBefore = now.AddMinutes(2) < plan.ExpiresAt ? now.AddMinutes(2) : plan.ExpiresAt;
        if (identity.ValidUntil < startBefore) startBefore = identity.ValidUntil;
        var authorizationExpires = now.AddMinutes(10) < identity.ValidUntil ? now.AddMinutes(10) : identity.ValidUntil;
        var record = new TaskPlanApproval(Guid.NewGuid(), planId, digest, TaskApprovalState.Granted, 1,
            identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.ApproverIdentity,
            identity.AuthenticationMethod, identity.PolicyRevision, now, startBefore, authorizationExpires, null, capabilities, null);
        var stored = new StoredApproval(record, keyReference, requestReference, [new("granted", null, now)]);
        WriteApproval(connection, transaction, stored, insert: true);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.ApprovalWritten);
        transaction.Commit();
        return record;
    });

    /// <summary>Consumes one matching approval and allocates one queued run in the same durable transaction.</summary>
    public TaskRun StartRun(Guid planId, Guid approvalId, string digest, TaskExecutionIdentity identity, string key) => Guard(() =>
    {
        ValidateIdentity(identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.PolicyRevision);
        var keyReference = ExecutionKey(identity.CanonicalSubject, key);
        var requestReference = Fingerprint(new { planId, approvalId, digest, identity });
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var retried = ReadRunRequest(connection, transaction, keyReference);
        if (retried is not null)
        {
            if (retried.RequestReference != requestReference || retried.SubjectReference != Reference(identity.CanonicalSubject))
                throw new TaskPlanStoreException("task.idempotency_conflict", 409);
            return ReadRun(connection, transaction, retried.RunId, identity.CanonicalSubject)?.Record ?? throw new InvalidDataException();
        }
        var approval = ReadApproval(connection, transaction, approvalId, identity.CanonicalSubject)
            ?? throw new TaskPlanStoreException("task.approval_not_found", 404);
        var authority = approval.Record;
        if (authority.PlanId != planId || authority.PlanDigest != digest) throw new TaskPlanStoreException("task.approval_plan_mismatch", 409);
        RequireSameIdentity(authority, identity);
        if (authority.State == TaskApprovalState.Consumed && authority.RunId is { } existingId)
        {
            var existing = ReadRun(connection, transaction, existingId, identity.CanonicalSubject) ?? throw new InvalidDataException();
            AddRunRequest(connection, transaction, new(keyReference, requestReference, Reference(identity.CanonicalSubject), existingId));
            transaction.Commit();
            return existing.Record;
        }
        var now = _clock.GetUtcNow();
        if (authority.State != TaskApprovalState.Granted || now >= authority.StartBefore || now >= authority.RunAuthorizationExpiresAt)
            throw new TaskPlanStoreException("task.approval_unavailable", 403);
        var plan = RequireCurrentPlan(connection, transaction, planId, identity.CanonicalSubject, digest);
        if (!ProposalCapabilities(plan.Plan).SequenceEqual(authority.Capabilities)) throw new TaskPlanStoreException("task.approval_plan_mismatch", 409);
        using (var running = Command(connection, transaction, "SELECT COUNT(*) FROM task_runs WHERE plan_id=$plan;", ("$plan", planId.ToString("D"))))
            if ((long)running.ExecuteScalar()! != 0) throw new TaskPlanStoreException("task.plan_already_started", 409);
        var runId = Guid.NewGuid();
        var record = new TaskRun(runId, planId, digest, approvalId, identity.CanonicalSubject, identity.SessionIdentity,
            identity.HostId, identity.PolicyRevision, TaskRunState.Queued, 1, false, now,
            plan.Plan.Semantics.Steps.Select(step => new TaskStepRun(step.StepId, TaskStepRunState.Pending,
                Fingerprint(new { host = identity.HostId, runId, step.StepId, purpose = "execute" }), null, null, null)).ToImmutableArray(), null);
        var consumed = approval with { Record = authority with { State = TaskApprovalState.Consumed, RunId = runId, Revision = authority.Revision + 1 },
            Audit = [.. approval.Audit, new("consumed", null, now)] };
        WriteApproval(connection, transaction, consumed, insert: false);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.ApprovalConsumed);
        InsertRun(connection, transaction, new(record, keyReference, requestReference, [new("queued", null, now)], [], [], [], []));
        AddRunRequest(connection, transaction, new(keyReference, requestReference, Reference(identity.CanonicalSubject), runId));
        _beforeCommit?.Invoke(TaskPlanCommitPoint.RunWritten);
        transaction.Commit();
        return record;
    });

    public TaskPlanApproval? GetApproval(Guid id, string subject) => Guard(() =>
    {
        if (!ValidSubject(subject)) return null;
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadApproval(connection, transaction, id, subject);
        if (stored is null) return null;
        if (stored.Record.State == TaskApprovalState.Granted && stored.Record.StartBefore <= _clock.GetUtcNow())
        {
            stored = stored with { Record = stored.Record with { State = TaskApprovalState.Expired, Revision = stored.Record.Revision + 1 },
                Audit = [.. stored.Audit, new("expired", null, _clock.GetUtcNow())] };
            WriteApproval(connection, transaction, stored, insert: false);
        }
        transaction.Commit(); return stored.Record;
    });

    public TaskRun? GetRun(Guid id, string subject) => Guard(() =>
    {
        if (!ValidSubject(subject)) return null;
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        return ReadRun(connection, transaction, id, subject)?.Record;
    });

    public TaskPlanApproval RevokeApproval(Guid id, string subject) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ValidSubject(subject) ? ReadApproval(connection, transaction, id, subject) : null;
        if (stored is null) throw new TaskPlanStoreException("task.approval_not_found", 404);
        if (stored.Record.State != TaskApprovalState.Revoked)
        {
            var now = _clock.GetUtcNow();
            stored = stored with { Record = stored.Record with { State = TaskApprovalState.Revoked,
                RevokedAt = now, Revision = stored.Record.Revision + 1 }, Audit = [.. stored.Audit, new("revoked", null, now)] };
            WriteApproval(connection, transaction, stored, insert: false);
        }
        _beforeCommit?.Invoke(TaskPlanCommitPoint.ApprovalWritten);
        transaction.Commit(); return stored.Record;
    });

    private TaskPlanRecord RequireCurrentPlan(SqliteConnection connection, SqliteTransaction transaction, Guid id, string subject, string digest)
    {
        var record = Read(connection, transaction, id, subject)?.Record ?? throw new TaskPlanStoreException("task.plan_not_found", 404);
        if (record.Plan.PlanDigest != digest) throw new TaskPlanStoreException("task.plan_digest_conflict", 409);
        if (record.Validity != TaskPlanValidity.Resolved || record.ExpiresAt <= _clock.GetUtcNow())
            throw new TaskPlanStoreException("task.plan_expired", 409);
        return record;
    }

    internal static ImmutableArray<TaskCapabilityTarget> ProposalCapabilities(TaskPlan plan)
    {
        if (plan.Semantics.Steps.Any(step => step.Disposition is not (TaskStepDisposition.Ready or TaskStepDisposition.NoOp)))
            throw new TaskPlanStoreException("task.plan_blocked", 409);
        foreach (var step in plan.Semantics.Steps)
        {
            if (step.Disposition == TaskStepDisposition.NoOp && step.RiskClass == "read-only" && step.RequiredCapabilities.IsEmpty
                && step.SchemaVersion == 1 && step.ImplementationRevision == "sing-box-read-1"
                && step.ActionId is "runtime.verifyInstallation" or "runtime.verifyCatalog") continue;
            if (step.SchemaVersion != 1 || step.ImplementationRevision != RuntimeChangePlanningActions.ImplementationRevision
                || !Enum.GetValues<RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange>()
                    .Where(change => change != RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Configure)
                    .Any(change => RuntimeChangePlanningActions.ActionId(change) == step.ActionId)
                || step.RequiredCapabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }])
                throw new TaskPlanStoreException("task.action_revision_unsupported", 409);
            RuntimeTaskFrozenChange frozen;
            try { frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs); }
            catch (TaskPlanningException) { throw new TaskPlanStoreException("task.plan_blocked", 409); }
            if (frozen is not { RuntimeId: "sing-box", Selection: not null, Impact: not null }
                || !Enum.IsDefined(frozen.Change) || frozen.Change == RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Configure
                || RuntimeChangePlanningActions.ActionId(frozen.Change) != step.ActionId || frozen.Selection.HostId != plan.Semantics.HostId
                || frozen.Selection.InstallationId == Guid.Empty || step.Disposition != TaskStepDisposition.Ready
                || step.RiskClass != (frozen.Impact.DeletesBusinessData ? "destructive" : "host-management")
                || step.RetryPolicy != "domain-idempotent" || step.CompensationKind != "domain-transaction")
                throw new TaskPlanStoreException("task.plan_blocked", 409);
        }
        var capabilities = plan.Semantics.Steps.SelectMany(step => step.RequiredCapabilities).Distinct()
            .OrderBy(item => item.Capability, StringComparer.Ordinal).ThenBy(item => item.Target, StringComparer.Ordinal).ToImmutableArray();
        if (capabilities.IsEmpty) throw new TaskPlanStoreException("task.approval_not_required", 409);
        return capabilities;
    }

    private static void RequireSameIdentity(TaskPlanApproval approval, TaskExecutionIdentity identity)
    {
        if (approval.CanonicalSubject != identity.CanonicalSubject || approval.SessionIdentity != identity.SessionIdentity
            || approval.HostId != identity.HostId || approval.PolicyRevision != identity.PolicyRevision)
            throw new TaskPlanStoreException("task.approval_identity_invalid", 403);
    }
    private static void ValidateIdentity(params string[] values)
    { if (values.Any(value => !ValidSubject(value))) throw new TaskPlanStoreException("task.approval_identity_invalid", 403); }
    private static string ExecutionKey(string subject, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new TaskPlanStoreException("task.idempotency_required", 400);
        return Fingerprint(new { subject, key });
    }
    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        TaskPlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(value, Json))));
    private void RevokePlanApprovals(SqliteConnection connection, SqliteTransaction transaction, Guid planId, string subject)
    {
        var records = new List<StoredApproval>();
        using (var query = Command(connection, transaction,
            "SELECT approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record FROM task_approvals WHERE plan_id=$plan AND subject_reference=$subject;",
            ("$plan", planId.ToString("D")), ("$subject", Reference(subject))))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) records.Add(DecodeApproval(reader));
        foreach (var stored in records.Where(item => item.Record.State != TaskApprovalState.Revoked))
        {
            var now = _clock.GetUtcNow();
            WriteApproval(connection, transaction, stored with
            {
                Record = stored.Record with { State = TaskApprovalState.Revoked, RevokedAt = now, Revision = stored.Record.Revision + 1 },
                Audit = [.. stored.Audit, new("revoked", null, now)]
            }, insert: false);
            _beforeCommit?.Invoke(TaskPlanCommitPoint.ApprovalWritten);
        }
    }
    private sealed record ExecutionAudit(string Event, string? StepId, DateTimeOffset At);
    private sealed record StoredApproval(TaskPlanApproval Record, string IdempotencyReference, string RequestReference, ImmutableArray<ExecutionAudit> Audit);
    private sealed record StoredRun(TaskRun Record, string IdempotencyReference, string RequestReference,
        ImmutableArray<ExecutionAudit> Audit, ImmutableArray<TaskDispatchIntent> Dispatches,
        ImmutableArray<TaskReconciliationEvidence> Results, ImmutableArray<TaskReadStepEvidence> ReadResults,
        ImmutableArray<TaskReconciliationEvidence> RecoveryResults);
    private sealed record RunRequest(string IdempotencyReference, string RequestReference, string SubjectReference, Guid RunId);
}
