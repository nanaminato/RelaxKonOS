using System.Text.Json;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed partial class TaskPlanStore
{
    private T DecodeExecution<T>(byte[] bytes)
    {
        if (bytes.Length > 524288) throw new InvalidDataException();
        using var document = JsonDocument.Parse(_protector.Unprotect(bytes));
        TaskPlanCanonicalizer.Canonicalize(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException();
    }
    private byte[] EncodeExecution<T>(T value)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        return bytes.Length <= 524288 ? bytes : throw new InvalidDataException();
    }

    private StoredApproval DecodeApproval(SqliteDataReader reader)
    {
        var stored = DecodeExecution<StoredApproval>((byte[])reader[6]);
        var record = stored.Record;
        var consumed = !stored.Audit.IsDefault && stored.Audit.Any(item => item?.Event == "consumed");
        if (record is null || record.ApprovalId == Guid.Empty || record.PlanId == Guid.Empty
            || record.ApprovalId.ToString("D") != reader.GetString(0) || record.PlanId.ToString("D") != reader.GetString(1)
            || !ValidSubject(record.CanonicalSubject) || Reference(record.CanonicalSubject) != reader.GetString(2)
            || stored.IdempotencyReference != reader.GetString(3) || stored.RequestReference != reader.GetString(4)
            || !Hash(stored.IdempotencyReference) || !Hash(stored.RequestReference) || !Hash(record.PlanDigest)
            || record.Revision != reader.GetInt64(5) || record.Revision is < 1 or > 3
            || !Enum.IsDefined(record.State) || !ValidSubject(record.SessionIdentity) || !ValidSubject(record.HostId)
            || !ValidSubject(record.ApproverIdentity) || !ValidSubject(record.AuthenticationMethod) || !ValidSubject(record.PolicyRevision)
            || record.StartBefore <= record.CreatedAt || record.StartBefore > record.CreatedAt.AddMinutes(2)
            || record.RunAuthorizationExpiresAt < record.StartBefore || record.RunAuthorizationExpiresAt > record.CreatedAt.AddMinutes(10)
            || record.Capabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }]
            || stored.Audit.IsDefault || stored.Audit.Length != record.Revision || stored.Audit.Any(item => item is null || item.StepId is not null)
            || consumed != (record.RunId is not null) || record.RunId == Guid.Empty
            || (record.State == TaskApprovalState.Revoked) != (record.RevokedAt is not null))
            throw new InvalidDataException();
        var events = string.Join(',', stored.Audit.Select(item => item.Event));
        if (record.State switch
            {
                TaskApprovalState.Granted => events != "granted",
                TaskApprovalState.Consumed => events != "granted,consumed",
                TaskApprovalState.Expired => events != "granted,expired",
                TaskApprovalState.Revoked => events is not ("granted,revoked" or "granted,expired,revoked" or "granted,consumed,revoked"),
                _ => true
            }) throw new InvalidDataException();
        return stored;
    }

    private StoredApproval? ReadApproval(SqliteConnection connection, SqliteTransaction transaction, Guid id, string subject)
    {
        StoredApproval stored;
        using (var query = Command(connection, transaction,
            "SELECT approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record FROM task_approvals WHERE approval_id=$id AND subject_reference=$subject;",
            ("$id", id.ToString("D")), ("$subject", Reference(subject))))
        using (var reader = query.ExecuteReader())
        { if (!reader.Read()) return null; stored = DecodeApproval(reader); }
        var plan = Read(connection, transaction, stored.Record.PlanId, subject) ?? throw new InvalidDataException();
        if (stored.Record.PlanDigest != plan.Record.Plan.PlanDigest || stored.Record.HostId != plan.Record.Plan.Semantics.HostId
            || stored.Record.CreatedAt < plan.Record.Plan.CreatedAt || stored.Record.StartBefore > plan.Record.ExpiresAt
            || !stored.Record.Capabilities.SequenceEqual(plan.Record.Plan.Semantics.Steps.SelectMany(step => step.RequiredCapabilities)
                .Distinct().OrderBy(item => item.Capability, StringComparer.Ordinal).ThenBy(item => item.Target, StringComparer.Ordinal)))
            throw new InvalidDataException();
        return stored;
    }

    private StoredRun? ReadRun(SqliteConnection connection, SqliteTransaction transaction, Guid id, string subject)
    {
        StoredRun stored;
        using (var query = Command(connection, transaction,
            "SELECT run_id,approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record FROM task_runs WHERE run_id=$id AND subject_reference=$subject;",
            ("$id", id.ToString("D")), ("$subject", Reference(subject))))
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            stored = DecodeExecution<StoredRun>((byte[])reader[7]);
            var run = stored.Record;
            if (run is null || run.RunId == Guid.Empty || run.RunId.ToString("D") != reader.GetString(0)
                || run.ApprovalId.ToString("D") != reader.GetString(1) || run.PlanId.ToString("D") != reader.GetString(2)
                || !ValidSubject(run.CanonicalSubject) || Reference(run.CanonicalSubject) != reader.GetString(3)
                || stored.IdempotencyReference != reader.GetString(4) || stored.RequestReference != reader.GetString(5)
                || !Hash(stored.IdempotencyReference) || !Hash(stored.RequestReference) || !Hash(run.PlanDigest)
                || run.Revision != reader.GetInt64(6) || run.Revision is < 1 or > 194
                || run.State is not (TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling or TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Cancelled or TaskRunState.NeedsRecovery)
                || run.Steps.IsDefaultOrEmpty
                || run.Steps.Any(step => step is null || step.State is not (TaskStepRunState.Pending or TaskStepRunState.Dispatching or TaskStepRunState.Running
                    or TaskStepRunState.Succeeded or TaskStepRunState.NoOp or TaskStepRunState.Failed or TaskStepRunState.Cancelled or TaskStepRunState.NeedsRecovery or TaskStepRunState.Skipped)
                    || !Hash(step.IdempotencyKey))) throw new InvalidDataException();
        }
        var approval = ReadApproval(connection, transaction, stored.Record.ApprovalId, subject) ?? throw new InvalidDataException();
        var plan = Read(connection, transaction, stored.Record.PlanId, subject) ?? throw new InvalidDataException();
        var record = stored.Record;
        if (approval.Record.RunId != record.RunId || approval.Record.PlanId != record.PlanId || approval.Record.PlanDigest != record.PlanDigest
            || approval.Record.CanonicalSubject != record.CanonicalSubject || approval.Record.SessionIdentity != record.SessionIdentity
            || approval.Record.HostId != record.HostId || approval.Record.PolicyRevision != record.PolicyRevision
            || record.CreatedAt < approval.Record.CreatedAt || record.CreatedAt >= approval.Record.StartBefore
            || !record.Steps.Select(step => step.StepId).SequenceEqual(plan.Record.Plan.Semantics.Steps.Select(step => step.StepId))
            || record.Steps.Any(step => step.IdempotencyKey != Fingerprint(new { host = record.HostId, runId = record.RunId, step.StepId, purpose = "execute" })))
            throw new InvalidDataException();
        ValidateRunJournal(stored, plan.Record.Plan);
        return stored;
    }

    private void WriteApproval(SqliteConnection connection, SqliteTransaction transaction, StoredApproval stored, bool insert)
    {
        var record = stored.Record;
        using var command = Command(connection, transaction, insert
                ? "INSERT INTO task_approvals(approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record) VALUES($id,$plan,$subject,$key,$request,$revision,$record);"
                : "UPDATE task_approvals SET revision=$revision,protected_record=$record WHERE approval_id=$id AND plan_id=$plan AND subject_reference=$subject AND idempotency_reference=$key AND request_reference=$request;",
            ("$id", record.ApprovalId.ToString("D")), ("$plan", record.PlanId.ToString("D")), ("$subject", Reference(record.CanonicalSubject)),
            ("$key", stored.IdempotencyReference), ("$request", stored.RequestReference), ("$revision", record.Revision), ("$record", EncodeExecution(stored)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
    }
    private void InsertRun(SqliteConnection connection, SqliteTransaction transaction, StoredRun stored)
    {
        var run = stored.Record;
        using var command = Command(connection, transaction,
            "INSERT INTO task_runs(run_id,approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record) VALUES($id,$approval,$plan,$subject,$key,$request,$revision,$record);",
            ("$id", run.RunId.ToString("D")), ("$approval", run.ApprovalId.ToString("D")), ("$plan", run.PlanId.ToString("D")),
            ("$subject", Reference(run.CanonicalSubject)), ("$key", stored.IdempotencyReference), ("$request", stored.RequestReference),
            ("$revision", run.Revision), ("$record", EncodeExecution(stored)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
    }

    private RunRequest? ReadRunRequest(SqliteConnection connection, SqliteTransaction transaction, string keyReference)
    {
        using var command = Command(connection, transaction,
            "SELECT run_id,protected_request FROM task_run_requests WHERE idempotency_reference=$key;", ("$key", keyReference));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var request = DecodeExecution<RunRequest>((byte[])reader[1]);
        if (request.IdempotencyReference != keyReference || request.RunId.ToString("D") != reader.GetString(0)
            || !Hash(request.RequestReference) || !Hash(request.SubjectReference)) throw new InvalidDataException();
        return request;
    }
    private void AddRunRequest(SqliteConnection connection, SqliteTransaction transaction, RunRequest request)
    {
        using (var quota = Command(connection, transaction, "SELECT COUNT(*) FROM task_run_requests WHERE run_id=$run;", ("$run", request.RunId.ToString("D"))))
            if ((long)quota.ExecuteScalar()! >= 64) throw new TaskPlanStoreException("task.run_request_capacity_exceeded", 409);
        using var command = Command(connection, transaction,
            "INSERT INTO task_run_requests(idempotency_reference,run_id,protected_request) VALUES($key,$run,$request);",
            ("$key", request.IdempotencyReference), ("$run", request.RunId.ToString("D")), ("$request", EncodeExecution(request)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
    }

    private void ValidateExecutionRecords(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var approvals = new List<(Guid Id, string Subject)>();
        using (var command = Command(connection, transaction,
            "SELECT approval_id,plan_id,subject_reference,idempotency_reference,request_reference,revision,protected_record FROM task_approvals;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (approvals.Count >= 1048576) throw new InvalidDataException();
                var stored = DecodeApproval(reader); approvals.Add((stored.Record.ApprovalId, stored.Record.CanonicalSubject));
            }
        foreach (var (id, subject) in approvals)
        {
            var approval = ReadApproval(connection, transaction, id, subject) ?? throw new InvalidDataException();
            if (approval.Record.RunId is { } runId && ReadRun(connection, transaction, runId, subject) is null)
                throw new InvalidDataException();
        }
        var runs = new List<(Guid Id, string Subject)>();
        using (var command = Command(connection, transaction, "SELECT run_id,subject_reference,protected_record FROM task_runs;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (runs.Count >= 65536) throw new InvalidDataException();
                var stored = DecodeExecution<StoredRun>((byte[])reader[2]);
                if (stored.Record.RunId.ToString("D") != reader.GetString(0) || Reference(stored.Record.CanonicalSubject) != reader.GetString(1))
                    throw new InvalidDataException();
                runs.Add((stored.Record.RunId, stored.Record.CanonicalSubject));
            }
        foreach (var (id, subject) in runs)
            if (ReadRun(connection, transaction, id, subject) is null) throw new InvalidDataException();
        var requests = new List<string>();
        using (var command = Command(connection, transaction, "SELECT idempotency_reference FROM task_run_requests;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            { if (requests.Count >= 4194304) throw new InvalidDataException(); requests.Add(reader.GetString(0)); }
        foreach (var key in requests)
        {
            var request = ReadRunRequest(connection, transaction, key) ?? throw new InvalidDataException();
            using var command = Command(connection, transaction, "SELECT subject_reference,request_reference FROM task_runs WHERE run_id=$run;", ("$run", request.RunId.ToString("D")));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || request.SubjectReference != reader.GetString(0) || request.RequestReference != reader.GetString(1))
                throw new InvalidDataException();
        }
        transaction.Commit();
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
