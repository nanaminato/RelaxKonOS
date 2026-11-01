using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Called only after the dispatcher's service-name and fixed allowlist checks.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNativeServiceActions
{
    internal static async Task<PrivilegedOperationResult> ApplyAsync(string name, PrivilegedServiceAction action)
    {
        if (action is not (PrivilegedServiceAction.Start or PrivilegedServiceAction.Stop or PrivilegedServiceAction.Restart))
            throw new ArgumentOutOfRangeException(nameof(action));
        using var service = new ServiceController(name);
        var elapsed = Stopwatch.StartNew();
        async Task Wait(ServiceControllerStatus status)
        {
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new System.TimeoutException();
            await Task.Run(() => service.WaitForStatus(status, remaining));
            service.Refresh();
        }
        if (service.Status == ServiceControllerStatus.StopPending) await Wait(ServiceControllerStatus.Stopped);
        if (service.Status == ServiceControllerStatus.StartPending) await Wait(ServiceControllerStatus.Running);
        if (action is PrivilegedServiceAction.Stop or PrivilegedServiceAction.Restart)
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                // An allowed target never grants authority over its dependent services.
                service.Stop(stopDependentServices: false);
                await Wait(ServiceControllerStatus.Stopped);
            }
        }
        if (action is PrivilegedServiceAction.Start or PrivilegedServiceAction.Restart)
        {
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                await Wait(ServiceControllerStatus.Running);
            }
        }
        return new(true);
    }
}
