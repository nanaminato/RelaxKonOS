using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Certificate;

internal sealed record CertificateOperationJournalEntry(CertificateOperationDto Operation, string IdempotencyKey, string? Actor);

/// <summary>SQLite mirror for lifecycle operation state; the file ledger remains a crash-safe
/// write-ahead recovery record while this journal is the queryable HostGlobal history.</summary>
internal sealed class HostOperationJournal
{
    private readonly string _connectionString;
    private readonly bool _enabled;

    public HostOperationJournal(IHostEnvironment environment, IConfiguration configuration)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.DatabasePath)}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async Task UpsertAsync(IEnumerable<CertificateOperationJournalEntry> entries, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var entry in entries)
        {
            var operation = entry.Operation;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO certificate_operations(operation_id,idempotency_key,certificate_id,kind,state,stage,problem_code,started_at,completed_at)
                VALUES($id,$key,$certificate,$kind,$state,$stage,$problem,$started,$completed)
                ON CONFLICT(operation_id) DO UPDATE SET state=excluded.state,stage=excluded.stage,problem_code=excluded.problem_code,started_at=excluded.started_at,completed_at=excluded.completed_at;
                """;
            command.Parameters.AddWithValue("$id", operation.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$key", entry.IdempotencyKey);
            command.Parameters.AddWithValue("$certificate", operation.CertificateId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$kind", operation.Kind);
            command.Parameters.AddWithValue("$state", operation.State.ToString());
            command.Parameters.AddWithValue("$stage", operation.Stage);
            command.Parameters.AddWithValue("$problem", operation.ProblemCode);
            command.Parameters.AddWithValue("$started", operation.StartedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$completed", operation.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var audit = connection.CreateCommand();
            audit.CommandText = """
                INSERT INTO certificate_audit_entries(audit_id,operation_id,certificate_id,actor,action,result,problem_code,created_at)
                VALUES($id,$operation,$certificate,$actor,$action,$result,$problem,$created)
                ON CONFLICT(audit_id) DO NOTHING;
                """;
            audit.Parameters.AddWithValue("$id", $"{operation.OperationId:D}:{operation.State}");
            audit.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            audit.Parameters.AddWithValue("$certificate", operation.CertificateId?.ToString("D") ?? (object)DBNull.Value);
            audit.Parameters.AddWithValue("$actor", (object?)entry.Actor ?? DBNull.Value);
            audit.Parameters.AddWithValue("$action", operation.Kind);
            audit.Parameters.AddWithValue("$result", operation.State.ToString());
            audit.Parameters.AddWithValue("$problem", string.IsNullOrEmpty(operation.ProblemCode) ? (object)DBNull.Value : operation.ProblemCode);
            audit.Parameters.AddWithValue("$created", (operation.CompletedAt ?? operation.StartedAt ?? DateTimeOffset.UtcNow).ToString("O"));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertWebAsync(IEnumerable<WebServerOperationJournalEntry> entries, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var entry in entries)
        {
            var operation = entry.Operation;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO webserver_operations(operation_id,idempotency_key,instance_id,kind,state,stage,problem_code,snapshot_id,started_at,completed_at)
                VALUES($id,$key,$instance,$kind,$state,$stage,$problem,$snapshot,$started,$completed)
                ON CONFLICT(operation_id) DO UPDATE SET state=excluded.state,stage=excluded.stage,problem_code=excluded.problem_code,snapshot_id=excluded.snapshot_id,started_at=excluded.started_at,completed_at=excluded.completed_at;
                """;
            command.Parameters.AddWithValue("$id", operation.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$key", entry.IdempotencyKey);
            command.Parameters.AddWithValue("$instance", operation.InstanceId);
            command.Parameters.AddWithValue("$kind", operation.Kind);
            command.Parameters.AddWithValue("$state", operation.State.ToString());
            command.Parameters.AddWithValue("$stage", operation.Stage);
            command.Parameters.AddWithValue("$problem", operation.ProblemCode);
            command.Parameters.AddWithValue("$snapshot", (object?)operation.SnapshotId ?? DBNull.Value);
            command.Parameters.AddWithValue("$started", operation.StartedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$completed", operation.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var audit = connection.CreateCommand();
            audit.CommandText = """
                INSERT INTO webserver_audit_entries(audit_id,operation_id,instance_id,actor,action,result,problem_code,created_at)
                VALUES($id,$operation,$instance,$actor,$action,$result,$problem,$created)
                ON CONFLICT(audit_id) DO NOTHING;
                """;
            audit.Parameters.AddWithValue("$id", $"{operation.OperationId:D}:{operation.State}");
            audit.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            audit.Parameters.AddWithValue("$instance", operation.InstanceId);
            audit.Parameters.AddWithValue("$actor", (object?)entry.Actor ?? DBNull.Value);
            audit.Parameters.AddWithValue("$action", operation.Kind);
            audit.Parameters.AddWithValue("$result", operation.State.ToString());
            audit.Parameters.AddWithValue("$problem", string.IsNullOrEmpty(operation.ProblemCode) ? (object)DBNull.Value : operation.ProblemCode);
            audit.Parameters.AddWithValue("$created", (operation.CompletedAt ?? operation.StartedAt ?? DateTimeOffset.UtcNow).ToString("O"));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

internal sealed record WebServerOperationJournalEntry(WebServerOperationDto Operation, string IdempotencyKey, string? Actor);
