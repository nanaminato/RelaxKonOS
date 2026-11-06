namespace RelaxKonOS.Server.ProxyServer;

/// <summary>A single expiry pass; it does not own the scheduler or the shared state gate.</summary>
public interface IProxyServerExpiryExecutor
{
    Task ReconcileExpiryAsync(CancellationToken ct);
}

/// <summary>Retains scheduling authority without retaining an inbound management instance.</summary>
public sealed class ProxyServerExpiryService(
    IServiceScopeFactory scopes,
    ProxyServerStore store,
    ILogger<ProxyServerExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (ProxyServerException) { logger.LogInformation("Proxy server credential expiry is not applied yet; the runtime or a pending transaction reported a problem."); }
                catch (Exception) { logger.LogWarning("Proxy server credential expiry could not be applied; inspect runtime and recovery state."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Keep scope/implementation locals off the long-lived timer state machine.
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var schedule = await store.ReadExpiryScheduleAsync(ct);
        if (!schedule.RequiresPass(DateTimeOffset.UtcNow)) return;
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProxyServerExpiryExecutor>().ReconcileExpiryAsync(ct);
    }
}
