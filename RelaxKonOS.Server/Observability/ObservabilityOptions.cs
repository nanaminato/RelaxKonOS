namespace RelaxKonOS.Server.Observability;

/// <summary>Explicit, safe-by-default observability configuration.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public string? InstanceId { get; init; }
    public string? LogDirectory { get; init; }
    public string? AuditDatabasePath { get; init; }
    public string? AuditHmacKey { get; init; }
    public string AuditKeyId { get; init; } = "v1";
    public int RuntimeLogRetentionDays { get; init; } = 30;
    public int AuditRetentionDays { get; init; } = 365;
    public long MaximumLogFileBytes { get; init; } = 16 * 1024 * 1024;
    public double SuccessfulRequestSampleRate { get; init; } = 0.05;
    public int SlowRequestThresholdMs { get; init; } = 1_000;
    public int ExternalOutputMaximumBytes { get; init; } = 4 * 1024;

    public void Validate(bool production)
    {
        if (RuntimeLogRetentionDays < 1 || AuditRetentionDays < 1 || MaximumLogFileBytes < 1024
            || SuccessfulRequestSampleRate is < 0 or > 1 || SlowRequestThresholdMs < 1 || ExternalOutputMaximumBytes is < 1 or > 1_048_576)
            throw new InvalidOperationException("Observability configuration contains an invalid retention, size, sampling, or threshold value.");
        if (LogDirectory is not null && !Path.IsPathFullyQualified(LogDirectory))
            throw new InvalidOperationException("Observability:LogDirectory must be an absolute path.");
        if (AuditDatabasePath is not null && !Path.IsPathFullyQualified(AuditDatabasePath))
            throw new InvalidOperationException("Observability:AuditDatabasePath must be an absolute path.");
        if (!production) return;
        if (AuditRetentionDays < 365 || string.IsNullOrWhiteSpace(InstanceId) || string.IsNullOrWhiteSpace(LogDirectory) || string.IsNullOrWhiteSpace(AuditDatabasePath)
            || string.IsNullOrWhiteSpace(AuditHmacKey))
            throw new InvalidOperationException("Production requires InstanceId, LogDirectory, AuditDatabasePath, AuditHmacKey, and at least 365 days of security audit retention.");
        try { if (Convert.FromBase64String(AuditHmacKey).Length < 32) throw new FormatException(); }
        catch (FormatException) { throw new InvalidOperationException("Observability:AuditHmacKey must be a base64-encoded 256-bit key or stronger."); }
    }
}
