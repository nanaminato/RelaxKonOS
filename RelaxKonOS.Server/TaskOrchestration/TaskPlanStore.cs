using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed class TaskPlanStoreException(string code, int statusCode = 503) : Exception(code)
{
    public string ProblemCode { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record TaskPlanStorageLimits(int MaximumPlans = 4096, int MaximumPlansPerSubject = 256,
    int LifetimeSeconds = 600);
public enum TaskPlanCommitPoint { PlanWritten, ValidityWritten, ApprovalWritten, ApprovalConsumed, RunWritten }

/// <summary>Durable plans, approval consumption and queued runs. Host execution still requires fresh external authorization.</summary>
public sealed partial class TaskPlanStore
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly TaskPlanStorageLimits _limits;
    private readonly Action<TaskPlanCommitPoint>? _beforeCommit;
    private readonly object _gate = new();
    private bool _unavailable;
    private static readonly JsonSerializerOptions Json = new(RelaxKonOSJsonOptions.Default)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    public TaskPlanStore(IHostEnvironment environment, IDataProtectionProvider protection)
        : this(Path.Combine(environment.ContentRootPath, "data", "task-plans.db"), protection) { }

    public TaskPlanStore(string path, IDataProtectionProvider protection, TimeProvider? clock = null,
        TaskPlanStorageLimits? limits = null, Action<TaskPlanCommitPoint>? beforeCommit = null)
    {
        _protector = protection.CreateProtector("RelaxKonOS.TaskOrchestration.Plans.v1");
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new();
        _beforeCommit = beforeCommit;
        if (_limits.MaximumPlans is < 1 or > 65536 || _limits.MaximumPlansPerSubject < 1
            || _limits.MaximumPlansPerSubject > _limits.MaximumPlans || _limits.LifetimeSeconds is < 1 or > 3600)
            throw new ArgumentException("task.storage_limits_invalid", nameof(limits));
        var absolute = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = absolute, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            var existed = File.Exists(absolute);
            using var connection = Open();
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!Equals(integrity.ExecuteScalar(), "ok")) throw new InvalidDataException();
            using (var schemaTransaction = connection.BeginTransaction(deferred: false))
            {
                using var version = connection.CreateCommand();
                version.Transaction = schemaTransaction;
                version.CommandText = "PRAGMA user_version;";
                var currentVersion = (long)version.ExecuteScalar()!;
                using var shape = connection.CreateCommand();
                shape.Transaction = schemaTransaction;
                shape.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('task_plans','task_approvals','task_runs','task_run_requests');";
                var currentTables = (long)shape.ExecuteScalar()!;
                if (existed || currentVersion != 0)
                { if (currentVersion != 2 || currentTables != 4) throw new InvalidDataException(); }
                else if (currentTables != 0) throw new InvalidDataException();
                using var schema = connection.CreateCommand();
                schema.Transaction = schemaTransaction;
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS task_plans (
                        plan_id TEXT PRIMARY KEY NOT NULL,
                        subject_reference TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        protected_record BLOB NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS task_plans_subject ON task_plans(subject_reference);
                    CREATE TABLE IF NOT EXISTS task_approvals (
                        approval_id TEXT PRIMARY KEY NOT NULL,
                        plan_id TEXT NOT NULL REFERENCES task_plans(plan_id),
                        subject_reference TEXT NOT NULL,
                        idempotency_reference TEXT UNIQUE NOT NULL,
                        request_reference TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        protected_record BLOB NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS task_runs (
                        run_id TEXT PRIMARY KEY NOT NULL,
                        approval_id TEXT UNIQUE NOT NULL REFERENCES task_approvals(approval_id),
                        plan_id TEXT UNIQUE NOT NULL REFERENCES task_plans(plan_id),
                        subject_reference TEXT NOT NULL,
                        idempotency_reference TEXT UNIQUE NOT NULL,
                        request_reference TEXT NOT NULL,
                        revision INTEGER NOT NULL,
                        protected_record BLOB NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS task_run_requests (
                        idempotency_reference TEXT PRIMARY KEY NOT NULL,
                        run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                        protected_request BLOB NOT NULL
                    );
                    PRAGMA user_version=2;
                    """;
                schema.ExecuteNonQuery();
                schemaTransaction.Commit();
            }
            // Validate every existing protected row before accepting new plan writes.
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT plan_id,subject_reference,revision,protected_record FROM task_plans;";
            using var reader = query.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (++count > 65536) throw new InvalidDataException();
                Decode(reader);
            }
            reader.Close();
            ValidateExecutionRecords(connection);
        }
        catch (Exception ex) when (StorageFailure(ex)) { _unavailable = true; }
    }

    public TaskPlanRecord Save(TaskPlan plan, string subject) => Guard(() =>
    {
        if (!ValidSubject(subject) || plan.Semantics.CanonicalSubject != subject)
            throw new TaskPlanStoreException("task.plan_not_found", 404);
        ValidatePlan(plan);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = Read(connection, transaction, plan.PlanId, subject);
        if (existing is not null)
        {
            if (!SamePlan(existing.Record.Plan, plan)) throw new TaskPlanStoreException("task.plan_identity_conflict", 409);
            if (existing.Record.Validity == TaskPlanValidity.Resolved && existing.Record.ExpiresAt <= _clock.GetUtcNow())
            {
                existing = Invalidated(existing, subject, "task.plan_expired");
                Write(connection, transaction, existing, subject, insert: false);
                _beforeCommit?.Invoke(TaskPlanCommitPoint.ValidityWritten);
            }
            transaction.Commit();
            return existing.Record;
        }
        // A colliding plan ID owned by another actor cannot be overwritten or revealed.
        using (var collision = Command(connection, transaction, "SELECT COUNT(*) FROM task_plans WHERE plan_id=$id;", ("$id", plan.PlanId.ToString("D"))))
            if ((long)collision.ExecuteScalar()! != 0) throw new TaskPlanStoreException("task.plan_identity_conflict", 409);
        using (var quota = Command(connection, transaction,
            "SELECT COUNT(*),COALESCE(SUM(CASE WHEN subject_reference=$subject THEN 1 ELSE 0 END),0) FROM task_plans;", ("$subject", Reference(subject))))
        using (var reader = quota.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt64(0) >= _limits.MaximumPlans || reader.GetInt64(1) >= _limits.MaximumPlansPerSubject)
                throw new TaskPlanStoreException("task.plan_capacity_exceeded", 409);
        }
        var now = _clock.GetUtcNow();
        if (plan.CreatedAt > now || plan.CreatedAt.AddSeconds(_limits.LifetimeSeconds) <= now)
            throw new TaskPlanStoreException("task.plan_expired", 409);
        var record = new TaskPlanRecord(plan, TaskPlanValidity.Resolved, 1,
            plan.CreatedAt.AddSeconds(_limits.LifetimeSeconds), null, null);
        var stored = new Stored(record, [new("created", Reference(subject), now)]);
        Write(connection, transaction, stored, subject, insert: true);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.PlanWritten);
        transaction.Commit();
        return record;
    });

    public TaskPlanRecord? Get(Guid id, string subject) => Guard(() =>
    {
        if (!ValidSubject(subject) || id == Guid.Empty) return null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = Read(connection, transaction, id, subject);
        if (stored is null) return null;
        if (stored.Record.Validity == TaskPlanValidity.Resolved && stored.Record.ExpiresAt <= _clock.GetUtcNow())
        {
            stored = Invalidated(stored, subject, "task.plan_expired");
            Write(connection, transaction, stored, subject, insert: false);
            _beforeCommit?.Invoke(TaskPlanCommitPoint.ValidityWritten);
        }
        transaction.Commit();
        return stored.Record;
    });

    public TaskPlanRecord Invalidate(Guid id, string subject, string digest, long expectedRevision) => Guard(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ValidSubject(subject) ? Read(connection, transaction, id, subject) : null;
        if (stored is null) throw new TaskPlanStoreException("task.plan_not_found", 404);
        if (stored.Record.Plan.PlanDigest != digest) throw new TaskPlanStoreException("task.plan_digest_conflict", 409);
        if (stored.Record.Revision != expectedRevision) throw new TaskPlanStoreException("task.plan_revision_conflict", 409);
        if (stored.Record.Validity == TaskPlanValidity.Resolved)
        {
            stored = Invalidated(stored, subject, stored.Record.ExpiresAt <= _clock.GetUtcNow() ? "task.plan_expired" : "task.plan_invalidated");
            Write(connection, transaction, stored, subject, insert: false);
            _beforeCommit?.Invoke(TaskPlanCommitPoint.ValidityWritten);
        }
        RevokePlanApprovals(connection, transaction, id, subject);
        transaction.Commit();
        return stored.Record;
    });

    private Stored Invalidated(Stored stored, string subject, string reason)
    {
        var now = _clock.GetUtcNow();
        return new(stored.Record with { Validity = TaskPlanValidity.Invalidated, Revision = stored.Record.Revision + 1,
            InvalidatedAt = now, ProblemCode = reason }, [.. stored.Audit, new(reason, Reference(subject), now)]);
    }

    private Stored? Read(SqliteConnection connection, SqliteTransaction transaction, Guid id, string subject)
    {
        using var query = Command(connection, transaction,
            "SELECT plan_id,subject_reference,revision,protected_record FROM task_plans WHERE plan_id=$id AND subject_reference=$subject;",
            ("$id", id.ToString("D")), ("$subject", Reference(subject)));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var stored = Decode(reader);
        if (stored.Record.Plan.Semantics.CanonicalSubject != subject) throw new InvalidDataException();
        return stored;
    }

    private Stored Decode(SqliteDataReader reader)
    {
        var bytes = (byte[])reader[3];
        if (bytes.Length > 524288) throw new InvalidDataException();
        var unprotected = _protector.Unprotect(bytes);
        using var document = JsonDocument.Parse(unprotected);
        TaskPlanCanonicalizer.Canonicalize(document.RootElement); // Includes duplicate-property rejection.
        var stored = document.RootElement.Deserialize<Stored>(Json) ?? throw new InvalidDataException();
        var record = stored.Record;
        ValidatePlan(record.Plan);
        if (record.Plan.PlanId.ToString("D") != reader.GetString(0)
            || Reference(record.Plan.Semantics.CanonicalSubject) != reader.GetString(1) || record.Revision != reader.GetInt64(2)
            || record.Revision is < 1 or > 2 || stored.Audit.IsDefault || stored.Audit.Length != record.Revision
            || record.ExpiresAt <= record.Plan.CreatedAt || record.ExpiresAt > record.Plan.CreatedAt.AddHours(1)
            || record.Validity == TaskPlanValidity.Resolved && (record.Revision != 1 || record.InvalidatedAt is not null || record.ProblemCode is not null)
            || record.Validity == TaskPlanValidity.Invalidated && (record.Revision != 2 || record.InvalidatedAt is null
                || record.ProblemCode is not ("task.plan_expired" or "task.plan_invalidated")) || !Enum.IsDefined(record.Validity)
            || stored.Audit.Any(item => item is null || item.SubjectReference != reader.GetString(1)))
            throw new InvalidDataException();
        if (stored.Audit[0].Event != "created" || stored.Audit[0].At < record.Plan.CreatedAt
            || record.Validity == TaskPlanValidity.Invalidated && (stored.Audit[1].Event != record.ProblemCode
                || stored.Audit[1].At != record.InvalidatedAt)) throw new InvalidDataException();
        return stored;
    }

    private void Write(SqliteConnection connection, SqliteTransaction transaction, Stored stored, string subject, bool insert)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        if (bytes.Length > 524288) throw new InvalidDataException();
        using var command = Command(connection, transaction, insert
            ? "INSERT INTO task_plans(plan_id,subject_reference,revision,protected_record) VALUES($id,$subject,$revision,$record);"
            : "UPDATE task_plans SET revision=$revision,protected_record=$record WHERE plan_id=$id AND subject_reference=$subject;",
            ("$id", stored.Record.Plan.PlanId.ToString("D")), ("$subject", Reference(subject)),
            ("$revision", stored.Record.Revision), ("$record", bytes));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
    }

    private static void ValidatePlan(TaskPlan plan)
    {
        if (plan.PlanId == Guid.Empty || !ValidSubject(plan.Semantics.CanonicalSubject)
            || plan.Semantics.Steps.IsDefaultOrEmpty || plan.PlanDigest != TaskPlanCanonicalizer.Digest(plan.Semantics)
            || TaskPlanCanonicalizer.Serialize(plan.Semantics).Length > TaskPlanLimits.Default.MaximumPlanBytes)
            throw new InvalidDataException();
    }
    private static bool SamePlan(TaskPlan first, TaskPlan second) =>
        TaskPlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(first, Json))
            .SequenceEqual(TaskPlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(second, Json)));
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private T Guard<T>(Func<T> action)
    {
        lock (_gate)
        {
            if (_unavailable) throw new TaskPlanStoreException("task.plan_store_unavailable");
            try { return action(); }
            catch (Exception ex) when (StorageFailure(ex))
            { _unavailable = true; throw new TaskPlanStoreException("task.plan_store_unavailable"); }
        }
    }
    private static bool StorageFailure(Exception ex) => ex is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException
        or CryptographicException or JsonException or TaskPlanningException or InvalidOperationException or ArgumentException or NullReferenceException;
    private static string Reference(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidSubject(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private sealed record Stored(TaskPlanRecord Record, ImmutableArray<Audit> Audit);
    private sealed record Audit(string Event, string SubjectReference, DateTimeOffset At);
}
