using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Recovers interrupted host proxy writes on startup and maintains the configured Windows/Linux proxy guard.</summary>
public sealed class SystemProxyGuardHostedService(IProxySettingsReader settings, IServiceScopeFactory scopes, ILogger<SystemProxyGuardHostedService> logger) : BackgroundService
{
    // Scheduling can stop immediately. An accepted reconciliation still owns its scope and response.
    public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var current = await settings.GetAsync(stoppingToken);
                var options = current.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
                delay = TimeSpan.FromSeconds(Math.Clamp(options.GuardIntervalSeconds, 5, 3_600));
                var problem = await ReconcileAsync(startup, current, stoppingToken);
                if (problem is null) startup = false;
                if (problem is not null) logger.LogWarning("System proxy reconciliation failed. ProblemCode={ProblemCode}", problem);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "System proxy guard iteration failed."); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<string?> ReconcileAsync(bool startup, ProxySettingsDto current, CancellationToken ct)
    {
        if (!startup && !(current.SystemProxyEnabled && current.SystemProxy?.GuardEnabled == true)) return null;
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProxySystemProxyReconciler>().ReconcileSystemProxyAsync(startup, ct);
    }
}
