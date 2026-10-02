using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Proxy.Platform;

public interface IHostSystemProxyService
{
    Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken cancellationToken);
}

public sealed class HostSystemProxyService(IPrivilegedOperationTransport transport) : IHostSystemProxyService
{
    public async Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return new(Environment.UserInteractive, Environment.UserInteractive, false, Environment.UserInteractive);
        if (!OperatingSystem.IsLinux()) return new(false, false, false, false);
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.LinuxSystemProxyRead), cancellationToken);
        return result.Success && result.SystemProxyCapabilities is { } capabilities ? capabilities : new(false, false, false, false);
    }

    public async Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken cancellationToken)
    {
        var options = settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        if (OperatingSystem.IsLinux())
        {
            if (settings.SystemProxyEnabled && options.UsePac) return ProxyProblemCodes.NotSupported;
            var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.LinuxSystemProxyApply,
                LinuxSystemProxy: new(settings.SystemProxyEnabled, settings.SystemProxyHost, settings.MixedPort,
                    options.UseDefaultBypass, options.BypassList, enforce)), cancellationToken);
            return result.Success ? null : result.ProblemCode switch
            {
                PrivilegedProblemCode.InvalidRequest => ProxyProblemCodes.ConfigInvalid,
                PrivilegedProblemCode.UnsupportedOperation => ProxyProblemCodes.NotSupported,
                PrivilegedProblemCode.Conflict => ProxyProblemCodes.SystemProxyConflict,
                _ => ProxyProblemCodes.PrivilegedOperationUnavailable
            };
        }
        if (!settings.SystemProxyEnabled && !previous.SystemProxyEnabled) return null;
        return Mihomo.MihomoSettingsService.ApplyWindowsSystemProxy(settings.SystemProxyEnabled,
            settings.SystemProxyHost, settings.MixedPort, options) ? null : ProxyProblemCodes.PrivilegedOperationUnavailable;
    }
}
