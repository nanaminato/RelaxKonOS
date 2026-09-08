namespace RelaxKonOS.Server.Identity;

/// <summary>Periodically removes expired in-memory refresh-token records.</summary>
public sealed class RefreshTokenCleanupService(AuthSessionStore sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            sessions.RemoveExpired();
    }
}
