using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Applies the small, safe set of Manager-owned Mihomo options to the protected config.</summary>
public sealed class MihomoSettingsService(
    IProxyPlatformPaths paths,
    IMihomoControllerClient controller,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions,
    ILogger<MihomoSettingsService>? logger = null) : IProxySettingsService, IProxyTunRuntimeController
{
    private static readonly HashSet<string> LogLevels = new(StringComparer.OrdinalIgnoreCase) { "silent", "error", "warning", "info", "debug" };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken) ?? Defaults;

    public async Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.MixedPort is < 1 or > 65535 || !LogLevels.Contains(request.LogLevel)) return ProxyProblemCodes.ConfigInvalid;
        if (request.SystemProxyEnabled && !OperatingSystem.IsWindows()) return ProxyProblemCodes.NotSupported;
        var systemProxyHost = NormalizeSystemProxyHost(request.SystemProxyHost);
        if (systemProxyHost is null) return ProxyProblemCodes.ConfigInvalid;
        var tun = request.Tun ?? ProxyTunSettingsDto.Default;
        if (!IsValidTunSettings(tun)) return ProxyProblemCodes.ConfigInvalid;
        var systemProxy = request.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        if (!IsValidSystemProxyOptions(systemProxy)) return ProxyProblemCodes.ConfigInvalid;
        var settings = new ProxySettingsDto(request.SystemProxyEnabled, request.AllowLan, request.DnsEnabled, request.Ipv6Enabled,
            request.UnifiedDelay, request.LogLevel.ToLowerInvariant(), request.MixedPort, request.AllowInsecureSubscriptionSources, systemProxyHost, tun, systemProxy);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await ReadAsync(cancellationToken) ?? Defaults;
            var directory = paths.GetProtectedConfigurationDirectory();
            var active = Path.Combine(directory, "active.yaml");
            if (!File.Exists(active))
            {
                // Subscription trust is host-level rather than a Mihomo YAML option. Let an
                // operator change it before importing a subscription or installing the runtime.
                if (!CanPersistWithoutRuntime(previous, settings)) return ProxyProblemCodes.RuntimeNotInstalled;
                await WriteAsync(settings, cancellationToken);
                return null;
            }
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            string updated;
            try
            {
                updated = MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithServerGeoDataSettings(
                        MihomoManagedConfiguration.WithRuntimeSettings(
                            MihomoManagedConfiguration.WithManagedTunSettings(original, settings), settings)), controllerOptions,
                    await controllerSecrets.GetOrCreateAsync(cancellationToken));
            }
            catch (ProxyControllerSecretException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }

            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                File.Move(temporary, active, overwrite: true);
                var controllerAvailable = (await controller.IsReachableAsync(cancellationToken)).Succeeded;
                var reload = controllerAvailable ? await controller.ReloadAsync(cancellationToken) : null;
                if (controllerAvailable && !string.IsNullOrEmpty(reload))
                {
                    await File.WriteAllTextAsync(active, original, cancellationToken);
                    await controller.ReloadAsync(cancellationToken);
                    return ProxyProblemCodes.ConfigApplyFailed;
                }
                // Updating unrelated Mihomo settings must not require a per-user Windows proxy
                // writer.  A transition that enables or disables the system proxy does.
                if ((settings.SystemProxyEnabled || previous.SystemProxyEnabled)
                    && !ApplyWindowsSystemProxy(settings.SystemProxyEnabled, settings.SystemProxyHost, settings.MixedPort, settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default))
                {
                    logger?.LogWarning("Proxy settings update could not apply the Windows system proxy. Enabled={Enabled} Port={Port}", settings.SystemProxyEnabled, settings.MixedPort);
                    await File.WriteAllTextAsync(active, original, cancellationToken);
                    if (controllerAvailable) await controller.ReloadAsync(cancellationToken);
                    return ProxyProblemCodes.PrivilegedOperationUnavailable;
                }
                await WriteAsync(settings, cancellationToken);
                logger?.LogInformation("Proxy settings updated. SystemProxyEnabled={SystemProxyEnabled} AllowLan={AllowLan} DnsEnabled={DnsEnabled} MixedPort={MixedPort}",
                    settings.SystemProxyEnabled, settings.AllowLan, settings.DnsEnabled, settings.MixedPort);
                return null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
        finally { _gate.Release(); }
    }

    public async Task<string?> SetEnabledAsync(RelaxKonOS.Server.Proxy.Platform.ProxyManagementRouteSnapshot snapshot, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
            if (!File.Exists(active)) return ProxyProblemCodes.RuntimeNotInstalled;
            var settings = await ReadAsync(cancellationToken) ?? Defaults;
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            var exclusions = enabled ? BuildTunRouteExclusions(snapshot) : [];
            var updated = MihomoManagedConfiguration.WithManagedTunSettings(original, settings, enabled, exclusions);
            var temporary = active + ".tun-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                SetPrivateFile(temporary);
                File.Move(temporary, active, overwrite: true);
                var reload = await controller.ReloadAsync(cancellationToken);
                if (string.IsNullOrEmpty(reload))
                {
                    // Mihomo's reload endpoint acknowledges the configuration before the TUN
                    // adapter is necessarily created.  On Windows, accepting that acknowledgement
                    // alone used to report a successful activation even after Wintun logged
                    // "Access is denied".  Require the configured adapter to become active.
                    var tunDeviceName = (settings.Tun ?? ProxyTunSettingsDto.Default).DeviceName;
                    if (enabled && !await WaitForWindowsTunDeviceAsync(tunDeviceName, cancellationToken))
                    {
                        await File.WriteAllTextAsync(active, original, cancellationToken);
                        SetPrivateFile(active);
                        await controller.ReloadAsync(CancellationToken.None);
                        logger?.LogWarning("Mihomo TUN configuration transition was rolled back because the Windows TUN adapter did not become active. DeviceName={DeviceName}", tunDeviceName);
                        return ProxyProblemCodes.TunActivationFailed;
                    }
                    logger?.LogInformation("Mihomo TUN configuration transition completed. Enabled={Enabled} EgressInterface={EgressInterface} ExclusionCount={ExclusionCount}",
                        enabled, snapshot.EgressInterface, exclusions.Count);
                    return null;
                }

                await File.WriteAllTextAsync(active, original, cancellationToken);
                SetPrivateFile(active);
                await controller.ReloadAsync(CancellationToken.None);
                logger?.LogWarning("Mihomo TUN configuration transition was rolled back after controller reload failed. Enabled={Enabled} ProblemCode={ProblemCode}", enabled, reload);
                return reload;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (IOException exception)
        {
            logger?.LogWarning(exception, "Mihomo TUN configuration transition failed because its protected configuration could not be written.");
            return ProxyProblemCodes.ConfigApplyFailed;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger?.LogWarning(exception, "Mihomo TUN configuration transition was denied access to its protected configuration.");
            return ProxyProblemCodes.PrivilegedOperationUnavailable;
        }
        finally { _gate.Release(); }
    }

    private async Task<ProxySettingsDto?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<ProxySettingsDto>(input, cancellationToken: cancellationToken);
            return settings is null ? null : settings with
            {
                Tun = settings.Tun ?? ProxyTunSettingsDto.Default,
                SystemProxy = settings.SystemProxy ?? ProxySystemProxyOptionsDto.Default,
            };
        }
        catch (JsonException) { return null; }
    }

    private async Task WriteAsync(ProxySettingsDto settings, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
        var path = Path.Combine(directory, "mihomo-settings.json");
        var temporary = path + ".new";
        await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, settings, cancellationToken: cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    internal static bool ApplyWindowsSystemProxy(bool enabled, string host, int port, ProxySystemProxyOptionsDto options)
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

    private static bool CanPersistWithoutRuntime(ProxySettingsDto previous, ProxySettingsDto updated) =>
        previous with
        {
            AllowInsecureSubscriptionSources = updated.AllowInsecureSubscriptionSources,
            Tun = updated.Tun,
            SystemProxy = updated.SystemProxy,
        } == updated;

    private static string? NormalizeSystemProxyHost(string? value)
    {
        if (string.Equals(value?.Trim(), "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback.ToString();
        if (!IPAddress.TryParse(value, out var address)) return null;
        if (IPAddress.IsLoopback(address)) return address.ToString();
        try
        {
            var isLocal = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
            return isLocal ? address.ToString() : null;
        }
        catch (NetworkInformationException) { return null; }
    }

    private static bool IsValidTunSettings(ProxyTunSettingsDto settings) =>
        settings.Stack is "system" or "gvisor" or "mixed"
        && Regex.IsMatch(settings.DeviceName, "^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)
        && settings.Mtu is >= 576 and <= 9000
        && IsValidDnsHijack(settings.DnsHijack)
        && (!settings.StrictRoute || settings.AutoRoute);

    private static bool IsValidSystemProxyOptions(ProxySystemProxyOptionsDto options) =>
        options.GuardIntervalSeconds is >= 5 and <= 3_600
        && options.BypassList.Length <= 4_096
        && options.BypassList.All(character => !char.IsControl(character));

    private static string BuildBypassList(ProxySystemProxyOptionsDto options)
    {
        const string defaults = "<local>;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*";
        var custom = options.BypassList.Trim().Trim(';');
        return options.UseDefaultBypass
            ? string.IsNullOrWhiteSpace(custom) ? defaults : defaults + ";" + custom
            : custom;
    }

    private static bool IsValidDnsHijack(string value)
    {
        var match = Regex.Match(value, "^(?:(?:tcp|udp)://)?(?:any|[0-9A-Fa-f:.]+):(\\d{1,5})$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535;
    }

    private static readonly ProxySettingsDto Defaults = new(false, false, true, true, false, "warning", 7890, false, "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);

    internal static IReadOnlyList<string> BuildTunRouteExclusions(RelaxKonOS.Server.Proxy.Platform.ProxyManagementRouteSnapshot snapshot)
    {
        var values = new List<string> { "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };
        if (IPAddress.TryParse(snapshot.DefaultGateway, out var gateway))
            values.Add(ToRoutePrefix(gateway));
        foreach (var value in snapshot.ManagementAddresses)
            if (IPAddress.TryParse(value, out var address) && !IPAddress.IsLoopback(address))
                values.Add(ToRoutePrefix(address));
        return values;
    }

    private static string ToRoutePrefix(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return address + "/32";

        // Windows represents a link-local IPv6 address as e.g. fe80::1%11. The
        // scope identifies a local interface, but it is not part of an IP prefix
        // and Mihomo correctly rejects fe80::1%11/128 as invalid CIDR syntax.
        var unscoped = address.ScopeId == 0 ? address : new IPAddress(address.GetAddressBytes());
        return unscoped + "/128";
    }

    private static void SetPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<bool> WaitForWindowsTunDeviceAsync(string deviceName, CancellationToken cancellationToken)
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
