namespace RelaxKonOS.Server.EventAlerts;

/// <summary>Runs bounded retention daily; active alerts are never selected for deletion.</summary>
public sealed class EventAlertRetentionService(EventAlertStore store, ILogger<EventAlertRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try { await store.RunRetentionAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Event-alert retention will retry on its next scheduled pass."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
