using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Keeps the user-level Windows proxy values aligned with the saved RelaxKonOS settings when the optional guard is enabled.</summary>
public sealed class SystemProxyGuardHostedService(IProxySettingsService settings, ILogger<SystemProxyGuardHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var current = await settings.GetAsync(stoppingToken);
                var options = current.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
                delay = TimeSpan.FromSeconds(Math.Clamp(options.GuardIntervalSeconds, 5, 3_600));
                if (OperatingSystem.IsWindows() && current.SystemProxyEnabled && options.GuardEnabled
                    && !MihomoSettingsService.ApplyWindowsSystemProxy(true, current.SystemProxyHost, current.MixedPort, options))
                    logger.LogWarning("System proxy guard could not update Windows Internet Settings.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "System proxy guard iteration failed."); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
