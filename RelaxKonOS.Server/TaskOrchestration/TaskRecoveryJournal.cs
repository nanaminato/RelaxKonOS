using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed partial class TaskPlanStore
{
    private sealed record StoredRecovery(TaskRecoveryAttempt Record, string IdempotencyReference, string RequestReference, DateTimeOffset? CompletedAt);

    public ImmutableArray<TaskRecoveryAttempt> GetRecoveryAttempts(Guid runId, string subject) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var run = ReadRun(connection, transaction, runId, subject)?.Record ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        return ReadRecoveryAttempts(connection, transaction, run).Select(item => item.Record).ToImmutableArray();
    });

    public TaskRecoveryAttempt? FindRecoveryAttempt(Guid runId, string subject, TaskRecoveryRequest request, string key) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        return FindRecoveryAttempt(connection, transaction, runId, subject, request, key)?.Record;
    });

    public (TaskRecoveryAttempt Attempt, bool Created) RecordRecoveryIntent(Guid runId, TaskRecoveryRequest request, TaskApprovalIdentity identity, string key) => Guard(() =>
    {
        ValidateIdentity(identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.PolicyRevision);
        if (!request.Confirmed) throw new TaskPlanStoreException("task.confirmation_required", 400);
        if (!Enum.IsDefined(request.Action) || request.OperationId == Guid.Empty || !ValidSubject(identity.ApproverIdentity)
            || !ValidSubject(identity.AuthenticationMethod)) throw new TaskPlanStoreException("task.recovery_request_invalid", 400);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        if (FindRecoveryAttempt(connection, transaction, runId, identity.CanonicalSubject, request, key) is { } previous) return (previous.Record, false);
        var run = ReadRun(connection, transaction, runId, identity.CanonicalSubject)?.Record ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        if (run.Revision != request.ExpectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.HostId != identity.HostId) throw new TaskPlanStoreException("task.policy_changed", 409);
        var step = RequireRecoveryStep(run, request.OperationId);
        if (ReadRecoveryAttempts(connection, transaction, run).Length >= 32) throw new TaskPlanStoreException("task.recovery_attempt_capacity_exceeded", 409);
        var now = _clock.GetUtcNow(); var until = identity.ValidUntil < now.AddMinutes(2) ? identity.ValidUntil : now.AddMinutes(2);
        if (until <= now) throw new TaskPlanStoreException("task.execution_authority_expired", 403);
        var record = new TaskRecoveryAttempt(Guid.NewGuid(), runId, step.StepId, request.OperationId, request.ExpectedRevision,
            request.Action, identity.CanonicalSubject, identity.SessionIdentity, identity.HostId, identity.PolicyRevision,
            identity.ApproverIdentity, identity.AuthenticationMethod, now, until, false, null);
        var stored = new StoredRecovery(record, ExecutionKey(identity.CanonicalSubject, key), RecoveryRequestDigest(runId, request), null);
        using var insert = Command(connection, transaction,
            "INSERT INTO task_recovery_attempts(attempt_id,run_id,subject_reference,idempotency_reference,request_reference,protected_record) VALUES($id,$run,$subject,$key,$request,$record);",
            ("$id", record.AttemptId.ToString("D")), ("$run", runId.ToString("D")), ("$subject", Reference(identity.CanonicalSubject)),
            ("$key", stored.IdempotencyReference), ("$request", stored.RequestReference), ("$record", EncodeExecution(stored)));
        insert.ExecuteNonQuery(); _beforeCommit?.Invoke(TaskPlanCommitPoint.RecoveryIntentWritten); transaction.Commit();
        return (record, true);
    });

    public TaskRecoveryAttempt RecordRecoveryResponse(Guid runId, string subject, Guid attemptId, string? problem) => Guard(() =>
    {
        if (problem is not null && (!problem.StartsWith("runtime.", StringComparison.Ordinal) || problem.Length > 128
            || problem.Any(c => !char.IsAsciiLetterLower(c) && c is not ('.' or '_')))) throw new ArgumentException("Invalid recovery problem.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, runId, subject)?.Record ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var stored = ReadRecoveryAttempts(connection, transaction, run).SingleOrDefault(item => item.Record.AttemptId == attemptId)
            ?? throw new TaskPlanStoreException("task.recovery_attempt_not_found", 404);
        if (stored.Record.ResponseRecorded) return stored.Record;
        var changed = stored with { Record = stored.Record with { ResponseRecorded = true, ProblemCode = problem }, CompletedAt = _clock.GetUtcNow() };
        using var update = Command(connection, transaction, "UPDATE task_recovery_attempts SET protected_record=$record WHERE attempt_id=$id;",
            ("$record", EncodeExecution(changed)), ("$id", attemptId.ToString("D")));
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException();
        _beforeCommit?.Invoke(TaskPlanCommitPoint.RecoveryResponseWritten); transaction.Commit(); return changed.Record;
    });

    internal static TaskStepRun RequireRecoveryStep(TaskRun run, Guid operationId) =>
        run.State == TaskRunState.NeedsRecovery && run.Steps.SingleOrDefault(step => step.State == TaskStepRunState.NeedsRecovery) is { } step
            && step.DomainOperationId == operationId && operationId != Guid.Empty ? step : throw new TaskPlanStoreException("task.recovery_not_required", 409);

    private static string RecoveryRequestDigest(Guid runId, TaskRecoveryRequest request) => Fingerprint(new
        { runId, request.ExpectedRevision, request.OperationId, request.Action, request.Confirmed });

    private StoredRecovery? FindRecoveryAttempt(SqliteConnection connection, SqliteTransaction transaction, Guid runId, string subject, TaskRecoveryRequest request, string key)
    {
        StoredRecovery stored;
        using (var query = Command(connection, transaction, "SELECT run_id,protected_record FROM task_recovery_attempts WHERE idempotency_reference=$key;", ("$key", ExecutionKey(subject, key))))
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            stored = DecodeExecution<StoredRecovery>((byte[])reader[1]);
            if (stored.Record.RunId.ToString("D") != reader.GetString(0)) throw new InvalidDataException();
        }
        if (stored.Record.RunId != runId || stored.Record.CanonicalSubject != subject || stored.RequestReference != RecoveryRequestDigest(runId, request))
            throw new TaskPlanStoreException("task.idempotency_conflict", 409);
        var run = ReadRun(connection, transaction, runId, subject)?.Record ?? throw new InvalidDataException();
        return ReadRecoveryAttempts(connection, transaction, run).Single(item => item.Record.AttemptId == stored.Record.AttemptId);
    }

    private ImmutableArray<StoredRecovery> ReadRecoveryAttempts(SqliteConnection connection, SqliteTransaction transaction, TaskRun run)
    {
        var items = ImmutableArray.CreateBuilder<StoredRecovery>();
        using var query = Command(connection, transaction,
            "SELECT attempt_id,subject_reference,idempotency_reference,request_reference,protected_record FROM task_recovery_attempts WHERE run_id=$run;", ("$run", run.RunId.ToString("D")));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            if (items.Count >= 32) throw new InvalidDataException();
            var stored = DecodeExecution<StoredRecovery>((byte[])reader[4]); var item = stored.Record;
            var step = run.Steps.SingleOrDefault(step => step.StepId == item.StepId);
            if (item.AttemptId == Guid.Empty || item.AttemptId.ToString("D") != reader.GetString(0) || item.RunId != run.RunId
                || item.CanonicalSubject != run.CanonicalSubject || Reference(item.CanonicalSubject) != reader.GetString(1)
                || !Hash(stored.IdempotencyReference) || stored.IdempotencyReference != reader.GetString(2)
                || stored.RequestReference != reader.GetString(3) || stored.RequestReference != RecoveryRequestDigest(run.RunId,
                    new() { ExpectedRevision = item.ExpectedRevision, OperationId = item.OperationId, Action = item.Action, Confirmed = true })
                || !Enum.IsDefined(item.Action) || item.ExpectedRevision < 1 || item.ExpectedRevision > run.Revision
                || step?.DomainOperationId != item.OperationId || item.OperationId == Guid.Empty || item.HostId != run.HostId
                || !ValidSubject(item.SessionIdentity) || !ValidSubject(item.PolicyRevision) || !ValidSubject(item.ApproverIdentity) || !ValidSubject(item.AuthenticationMethod)
                || item.CreatedAt < run.CreatedAt || item.ValidUntil <= item.CreatedAt || item.ValidUntil > item.CreatedAt.AddMinutes(2)
                || item.ResponseRecorded != (stored.CompletedAt is not null) || stored.CompletedAt < item.CreatedAt
                || !item.ResponseRecorded && item.ProblemCode is not null) throw new InvalidDataException();
            items.Add(stored);
        }
        return items.ToImmutable();
    }
}
