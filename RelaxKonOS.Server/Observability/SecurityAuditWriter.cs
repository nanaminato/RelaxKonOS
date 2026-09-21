using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

public interface ISecurityAuditWriter
{
    /// <summary>Returns false when durable persistence failed. Callers of critical changes must fail closed.</summary>
    Task<bool> TryWriteAsync(SecurityAuditEvent entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Separate append-only audit store. Application code exposes no update or delete operation.
/// Deployment must additionally enforce the documented OS/database ACLs.
/// </summary>
public sealed class SecurityAuditWriter(ObservabilityOptions options, IObservabilitySanitizer sanitizer, IEventLogger eventLogger)
    : ISecurityAuditWriter
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _key = string.IsNullOrWhiteSpace(options.AuditHmacKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(options.AuditHmacKey);

    public async Task<bool> TryWriteAsync(SecurityAuditEvent entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.AuditDatabasePath)) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(options.AuditDatabasePath)!;
            Directory.CreateDirectory(directory);
            await using var connection = new SqliteConnection($"Data Source={options.AuditDatabasePath};Mode=ReadWriteCreate");
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var previous = await ReadPreviousHashAsync(connection, transaction, cancellationToken);
            var normalized = entry with
            {
                ActorReference = sanitizer.ToReference(entry.ActorReference),
                ResourceReference = sanitizer.ToReference(entry.ResourceReference),
                ProblemCode = sanitizer.SanitizeSummary(entry.ProblemCode, 128)
            };
            var hash = ComputeHash(previous, normalized);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO security_audit_entries (id, timestamp_utc, event_id, event_name, outcome, component, instance_id,
                    correlation_id, operation_id, action, actor_reference, resource_type, resource_reference, problem_code,
                    duration_ms, previous_hash, entry_hash, key_id)
                VALUES ($id,$timestamp,$eventId,$eventName,$outcome,$component,$instanceId,$correlationId,$operationId,$action,
                    $actorReference,$resourceType,$resourceReference,$problemCode,$durationMs,$previousHash,$entryHash,$keyId);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$timestamp", normalized.TimestampUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$eventId", normalized.EventId);
            command.Parameters.AddWithValue("$eventName", normalized.EventName);
            command.Parameters.AddWithValue("$outcome", normalized.Outcome.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$component", normalized.Component);
            command.Parameters.AddWithValue("$instanceId", normalized.InstanceId);
            command.Parameters.AddWithValue("$correlationId", normalized.CorrelationId.ToString("D"));
            command.Parameters.AddWithValue("$operationId", normalized.OperationId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$action", normalized.Action ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$actorReference", normalized.ActorReference ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$resourceType", normalized.ResourceType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$resourceReference", normalized.ResourceReference ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$problemCode", normalized.ProblemCode ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$durationMs", normalized.DurationMs ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$previousHash", previous ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$entryHash", hash);
            command.Parameters.AddWithValue("$keyId", options.AuditKeyId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            eventLogger.Write(new(ObservabilityEventCatalog.AuditWriteFailed, ObservabilitySeverity.Critical, ObservabilityOutcome.Failed,
                "server", "Security audit persistence failed.", "observability.audit_unavailable", Exception: sanitizer.SanitizeException(exception)));
            return false;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS security_audit_entries (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, timestamp_utc TEXT NOT NULL,
                event_id INTEGER NOT NULL, event_name TEXT NOT NULL, outcome TEXT NOT NULL, component TEXT NOT NULL,
                instance_id TEXT NOT NULL, correlation_id TEXT NOT NULL, operation_id TEXT NULL, action TEXT NULL,
                actor_reference TEXT NULL, resource_type TEXT NULL, resource_reference TEXT NULL, problem_code TEXT NULL,
                duration_ms INTEGER NULL, previous_hash TEXT NULL, entry_hash TEXT NOT NULL, key_id TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_security_audit_correlation ON security_audit_entries(correlation_id);
            CREATE INDEX IF NOT EXISTS ix_security_audit_operation ON security_audit_entries(operation_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadPreviousHashAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entry_hash FROM security_audit_entries ORDER BY sequence DESC LIMIT 1;";
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private string ComputeHash(string? previous, SecurityAuditEvent entry)
    {
        var value = string.Join("\n", previous, entry.TimestampUtc.UtcDateTime.ToString("O"), entry.EventId, entry.EventName,
            entry.Outcome, entry.Component, entry.InstanceId, entry.CorrelationId, entry.OperationId, entry.Action,
            entry.ActorReference, entry.ResourceType, entry.ResourceReference, entry.ProblemCode, entry.DurationMs, options.AuditKeyId);
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
