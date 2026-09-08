using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Certificate;

internal sealed record CertificateRenewalRetrySchedule(int ConsecutiveFailures, DateTimeOffset? RetryAfter, bool Exhausted);

/// <summary>Persists automatic-renewal outcomes so exponential backoff survives process restarts.
/// The CA adapter may fail without exposing a Retry-After header; in that case this repository
/// supplies bounded exponential backoff plus jitter rather than immediately creating another order.</summary>
internal sealed class CertificateRenewalAttemptRepository
{
    private readonly string _connectionString;
    private readonly bool _enabled;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly SemaphoreSlim _memoryGate = new(1, 1);
    private readonly List<Attempt> _memoryAttempts = [];

    public CertificateRenewalAttemptRepository(IHostEnvironment environment, IConfiguration configuration, CertificateOptions options)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.DatabasePath)}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
        _maximumAttempts = Math.Clamp(options.RenewalRetryMaxAttempts, 1, 12);
        _baseDelay = TimeSpan.FromMinutes(Math.Clamp(options.RenewalRetryBaseDelayMinutes, 1, 60));
    }

    public async Task<CertificateRenewalRetrySchedule> GetScheduleAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            await _memoryGate.WaitAsync(cancellationToken);
            try { return Calculate(_memoryAttempts.Where(item => item.CertificateId == certificateId).OrderByDescending(item => item.CompletedAt)); }
            finally { _memoryGate.Release(); }
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_at, problem_code, retry_after FROM certificate_renewal_attempts WHERE certificate_id=$id ORDER BY completed_at DESC;";
        command.Parameters.AddWithValue("$id", certificateId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attempts = new List<Attempt>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var completedAt = reader.IsDBNull(0) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(reader.GetString(0));
            var problem = reader.IsDBNull(1) ? null : reader.GetString(1);
            DateTimeOffset? retryAfter = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2));
            attempts.Add(new Attempt(certificateId, completedAt, problem, retryAfter));
        }
        return Calculate(attempts);
    }

    public async Task RecordAsync(CertificateOperationDto operation, CancellationToken cancellationToken)
    {
        if (operation.Kind != "renew" || operation.CertificateId is not { } certificateId || operation.State is not (CertificateOperationState.Succeeded or CertificateOperationState.Failed))
            return;

        var now = operation.CompletedAt ?? DateTimeOffset.UtcNow;
        var prior = await GetScheduleAsync(certificateId, cancellationToken);
        var problem = operation.State == CertificateOperationState.Failed ? operation.ProblemCode : null;
        DateTimeOffset? retryAfter = problem is null ? null : NextRetryAfter(now, prior.ConsecutiveFailures);
        var attempt = new Attempt(certificateId, now, problem, retryAfter);
        if (!_enabled)
        {
            await _memoryGate.WaitAsync(cancellationToken);
            try { _memoryAttempts.Add(attempt); }
            finally { _memoryGate.Release(); }
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO certificate_renewal_attempts(attempt_id,certificate_id,operation_id,started_at,completed_at,problem_code,retry_after)
            VALUES($id,$certificate,$operation,$started,$completed,$problem,$retryAfter);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$certificate", certificateId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$started", (operation.StartedAt ?? now).ToString("O"));
        command.Parameters.AddWithValue("$completed", now.ToString("O"));
        command.Parameters.AddWithValue("$problem", (object?)problem ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryAfter", retryAfter?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private CertificateRenewalRetrySchedule Calculate(IEnumerable<Attempt> newestFirst)
    {
        var failures = 0;
        DateTimeOffset? retryAfter = null;
        foreach (var attempt in newestFirst)
        {
            if (string.IsNullOrEmpty(attempt.ProblemCode)) break;
            failures++;
            retryAfter ??= attempt.RetryAfter;
        }
        return new CertificateRenewalRetrySchedule(failures, retryAfter, failures >= _maximumAttempts);
    }

    private DateTimeOffset NextRetryAfter(DateTimeOffset now, int priorFailures)
    {
        var exponent = Math.Min(priorFailures, 10);
        var delay = TimeSpan.FromTicks(_baseDelay.Ticks * (1L << exponent));
        var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Min(delay.TotalSeconds * 0.2, 60));
        return now.Add(delay + jitter);
    }

    private sealed record Attempt(Guid CertificateId, DateTimeOffset CompletedAt, string? ProblemCode, DateTimeOffset? RetryAfter);
}
