using System.Net;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Proxy.Platform;

public interface IHostSystemProxyService
{
    Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken cancellationToken);
}

internal interface ITrackedHostSystemProxy
{
    bool Supported { get; }
    Task<bool> ApplyTrackedAsync(Guid operationId, ProxySettingsDto settings, CancellationToken ct);
    Task<LinuxSystemProxyReceipt?> ObserveTrackedEffectAsync(Guid operationId, ProxySettingsDto settings, CancellationToken ct);
}

public sealed class HostSystemProxyService(IPrivilegedOperationTransport transport) : IHostSystemProxyService, ITrackedHostSystemProxy
{
    bool ITrackedHostSystemProxy.Supported => OperatingSystem.IsLinux();

    async Task<bool> ITrackedHostSystemProxy.ApplyTrackedAsync(Guid operationId, ProxySettingsDto settings, CancellationToken ct) =>
        await TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyApplyTracked, operationId, settings, ct) is { Applied: true, Current: true };

    Task<LinuxSystemProxyReceipt?> ITrackedHostSystemProxy.ObserveTrackedEffectAsync(Guid operationId, ProxySettingsDto settings, CancellationToken ct) =>
        TrackedAsync(PrivilegedOperationKind.LinuxSystemProxyReceipt, operationId, settings, ct);

    private async Task<LinuxSystemProxyReceipt?> TrackedAsync(PrivilegedOperationKind kind, Guid operationId, ProxySettingsDto settings, CancellationToken ct)
    {
        var options = settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        if (!OperatingSystem.IsLinux() || settings.SystemProxyEnabled && options.UsePac) return null;
        var result = await transport.ExecuteAsync(new(kind, OperationId: operationId,
            LinuxSystemProxy: new(settings.SystemProxyEnabled, settings.SystemProxyHost, settings.MixedPort,
                options.UseDefaultBypass, options.BypassList)), ct);
        return result is { Success: true, LinuxSystemProxyReceipt: { } receipt } && receipt.OperationId == operationId
            ? receipt : null;
    }

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
        return ApplyWindowsSystemProxy(settings.SystemProxyEnabled,
            settings.SystemProxyHost, settings.MixedPort, options) ? null : ProxyProblemCodes.PrivilegedOperationUnavailable;
    }
    private static bool ApplyWindowsSystemProxy(bool enabled, string host, int port, ProxySystemProxyOptionsDto options)
    {
        if (!OperatingSystem.IsWindows()) return !enabled;
        // HKCU belongs to the Server service account in System Mode, not the signed-in desktop
        // user.  Silently changing it is both ineffective and surprising.  A future per-user
        // companion must apply this setting in that user's session.
        if (!Environment.UserInteractive) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("AutoDetect", enabled && options.UsePac ? 1 : 0, RegistryValueKind.DWord);
            if (enabled)
            {
                var proxyHost = IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? "[" + host + "]"
                    : host;
                key.SetValue("ProxyServer", proxyHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                key.SetValue("ProxyOverride", BuildBypassList(options), RegistryValueKind.String);
            }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static string BuildBypassList(ProxySystemProxyOptionsDto options)
    {
        const string defaults = "<local>;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*";
        var custom = options.BypassList.Trim().Trim(';');
        return options.UseDefaultBypass
            ? string.IsNullOrWhiteSpace(custom) ? defaults : defaults + ";" + custom
            : custom;
    }

}
