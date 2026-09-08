using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace RelaxKonOS.Guardian.Agent;

/// <summary>
/// Monitors only the installer-declared RelaxKonOS Server service. User-managed workloads
/// never receive the elevated ability to restart a native service.
/// </summary>
internal sealed class ProtectedServerMonitor(GuardianAgentOptions options, ILogger<ProtectedServerMonitor> logger)
{
    private readonly ProtectedServerMonitorOptions _options = options.ProtectedServerMonitor;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            logger.LogInformation("Protected RelaxKonOS Server monitoring is not configured.");
            return;
        }

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                using var response = await client.GetAsync(_options.HealthUrl!, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    failures = 0;
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RelaxKonOS Server health probe failed.");
            }

            failures++;
            if (failures < _options.FailureThreshold) continue;
            failures = 0;
            var restarted = await RestartProtectedServiceAsync(cancellationToken);
            if (restarted)
                logger.LogWarning("Restarted protected RelaxKonOS Server service {ServiceName} after failed health probes.", _options.ServiceName);
            else
                logger.LogError("Could not restart protected RelaxKonOS Server service {ServiceName}.", _options.ServiceName);
        }
    }

    private async Task<bool> RestartProtectedServiceAsync(CancellationToken cancellationToken)
    {
        return OperatingSystem.IsWindows()
            ? await RestartWindowsServiceAsync(cancellationToken)
            : await RestartSystemdServiceAsync(cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> RestartWindowsServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var service = new ServiceController(_options.ServiceName!);
            service.Refresh();
            if (service.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
            {
                service.Stop();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)), cancellationToken);
            }
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)), cancellationToken);
            }
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Windows service restart failed for {ServiceName}.", _options.ServiceName);
            return false;
        }
    }

    private async Task<bool> RestartSystemdServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("systemctl")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("restart");
            process.StartInfo.ArgumentList.Add(_options.ServiceName!);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "systemd service restart failed for {ServiceName}.", _options.ServiceName);
            return false;
        }
    }
}
