namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Core-owned durable-state scan. Only operation identity survives between passes.</summary>
public sealed class MihomoPublicationRecoveryScheduler(IServiceScopeFactory scopes,
    ILogger<MihomoPublicationRecoveryScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? blocked = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { blocked = await ScanAsync(blocked, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or RelaxKonOS.ManagedRuntimes.RuntimePolicyException or RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException)
            { logger.LogWarning("Mihomo publication responsibility could not be verified; durable state remains authoritative."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task<Guid?> ScanAsync(Guid? blocked, CancellationToken ct)
    {
        var operation = await ReadPendingIdentityAsync(ct);
        if (operation is null) return null;
        if (operation == blocked) return blocked;
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MihomoPublicationRecoveryService>().ReconcileAsync(ct);
    }

    // Dispose the scan scope and discard the full durable snapshot before a retrying runner starts.
    private async Task<Guid?> ReadPendingIdentityAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<MihomoPublicationStore>().ReadAsync(ct)).Pending?.OperationId;
    }
}
