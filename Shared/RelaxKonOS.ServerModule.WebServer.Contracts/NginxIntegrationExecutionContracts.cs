namespace RelaxKonOS.Server.WebServer;

public sealed record NginxIntegrationPlan(string? Problem, string? SnapshotId, bool AlreadyIntegrated = false);
public sealed record NginxIntegrationResult(string Problem, string? SnapshotId);

public interface INginxIntegrationExecutor
{
    Task<NginxIntegrationResult> IntegrateAsync(CancellationToken ct);
}

public interface INginxIntegrationPorts
{
    Task<NginxIntegrationPlan> PrepareAsync(CancellationToken ct);
    Task<bool> IsSnapshotCurrentAsync(CancellationToken ct);
    Task<string?> StageAsync(CancellationToken ct);
    Task<bool> TestAsync(CancellationToken ct);
    Task CheckpointAsync(CancellationToken ct);
    Task RecordCommitBoundaryAsync(CancellationToken ct);
    Task<bool> CommitAsync();
    Task DispatchReloadAsync();
    Task CleanStageAsync();
}
