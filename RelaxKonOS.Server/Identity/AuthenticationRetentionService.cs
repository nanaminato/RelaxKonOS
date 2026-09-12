using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Identity;

public sealed class AuthenticationRetentionService(IServiceScopeFactory scopes, IOptions<AuthSecurityOptions> options,
    AuthSessionStore sessions, ILogger<AuthenticationRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            sessions.RemoveExpired();
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetService<RelaxKonOSDbContext>();
                if (db is null) continue;
                var retention = Math.Clamp(options.Value.SecurityEventRetentionDays, 1, 365);
                var maximum = Math.Clamp(options.Value.MaximumSecurityEvents, 1000, 1_000_000);
                var removed = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM authentication_security_events WHERE julianday(CreatedAt) < julianday('now', {-retention + " days"})", stoppingToken);
                removed += await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM authentication_security_events WHERE Id IN (SELECT Id FROM authentication_security_events ORDER BY CreatedAt DESC LIMIT -1 OFFSET {maximum})", stoppingToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM account_failure_states WHERE julianday(LastFailureAt) < julianday('now', {-Math.Clamp(options.Value.AccountFailureRetentionHours, 1, 168) + " hours"})", stoppingToken);
                if (removed != 0) logger.LogInformation("Authentication audit retention removed {Count} events", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Authentication retention unavailable; no credentials or policies were changed"); }
        }
    }
}
