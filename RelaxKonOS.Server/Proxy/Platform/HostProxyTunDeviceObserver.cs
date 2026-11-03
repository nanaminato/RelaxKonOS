using System.Diagnostics;
using System.Net.NetworkInformation;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>Checks the configured adapter after an acknowledged reload; it never changes network state.</summary>
public interface IProxyTunDeviceObserver
{
    Task<bool> WaitForActiveAsync(string deviceName, CancellationToken ct);
}

public sealed class HostProxyTunDeviceObserver : IProxyTunDeviceObserver
{
    public async Task<bool> WaitForActiveAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                if (NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                    string.Equals(network.Name, deviceName, StringComparison.OrdinalIgnoreCase)
                    && network.OperationalStatus == OperationalStatus.Up))
                    return true;
            }
            catch (NetworkInformationException) { return false; }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        return false;
    }
}
