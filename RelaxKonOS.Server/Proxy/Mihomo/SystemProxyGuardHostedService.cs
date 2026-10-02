using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Recovers interrupted host proxy writes on startup and maintains the configured Windows/Linux proxy guard.</summary>
public sealed class SystemProxyGuardHostedService(IProxySettingsService settings, ILogger<SystemProxyGuardHostedService> logger) : BackgroundService
{
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
                var problem = await settings.ReconcileSystemProxyAsync(startup, stoppingToken);
                if (problem is null) startup = false;
                if (problem is not null) logger.LogWarning("System proxy reconciliation failed. ProblemCode={ProblemCode}", problem);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "System proxy guard iteration failed."); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
