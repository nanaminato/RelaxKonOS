namespace RelaxKonOS.Server.WebServer;

public sealed class NginxIntegrationExecutor(INginxIntegrationPorts ports) : INginxIntegrationExecutor
{
    public async Task<NginxIntegrationResult> IntegrateAsync(CancellationToken ct)
    {
        var plan = await ports.PrepareAsync(ct);
        if (plan.Problem is not null || plan.AlreadyIntegrated)
            return new(plan.Problem ?? "", plan.SnapshotId);
        try
        {
            if (!await ports.IsSnapshotCurrentAsync(ct))
                return new("webserver.configuration_changed", plan.SnapshotId);
            await ports.CheckpointAsync(ct);
            if (await ports.StageAsync(ct) is { } problem)
                return new(problem, plan.SnapshotId);
            if (!await ports.IsSnapshotCurrentAsync(ct))
                return new("webserver.configuration_changed", plan.SnapshotId);
            if (!await ports.TestAsync(ct))
                return new("webserver.config_test_failed", plan.SnapshotId);
            if (!await ports.IsSnapshotCurrentAsync(ct))
                return new("webserver.configuration_changed", plan.SnapshotId);

            // Once this is durable, the Host can only reconcile the original native receipt.
            await ports.RecordCommitBoundaryAsync(ct);
            if (await ports.CommitAsync()) await ports.DispatchReloadAsync();
            return new(NginxReloadOutcome.RecoveryRequired, plan.SnapshotId);
        }
        finally { await ports.CleanStageAsync(); }
    }
}
