using System.Net;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Only live TUN configuration transitions; the caller retains the durable safety marker.</summary>
public sealed class MihomoTunRuntimeController(
    IProxyPlatformPaths paths,
    IProxySettingsReader settingsReader,
    IMihomoControllerClient controller,
    MihomoPublicationStore publication,
    Platform.IProxyTunDeviceObserver devices,
    ILogger<MihomoTunRuntimeController>? logger = null) : IProxyTunRuntimeController
{
    public async Task<string?> SetEnabledAsync(RelaxKonOS.Server.Proxy.Platform.ProxyManagementRouteSnapshot snapshot, bool enabled, CancellationToken cancellationToken)
    {
        await publication.Gate.WaitAsync(cancellationToken);
        try
        {
            if (await publication.UncoordinatedWriteProblemAsync(cancellationToken) is { } publicationProblem) return publicationProblem;
            var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
            if (!File.Exists(active)) return ProxyProblemCodes.RuntimeNotInstalled;
            var settings = await settingsReader.GetAsync(cancellationToken);
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            var exclusions = enabled ? BuildTunRouteExclusions(snapshot) : [];
            var updated = MihomoManagedConfiguration.WithManagedTunSettings(original, settings, enabled, exclusions);
            var temporary = active + ".tun-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                SetPrivateFile(temporary);
                File.Move(temporary, active, overwrite: true);
                // A recovery transition can run while Mihomo is intentionally stopped (for
                // example after the Windows Server process was restarted). Do not send a reload
                // to a controller that was already unavailable: ReloadAsync correctly gives an
                // in-flight reload up to 30 seconds to reconnect, but that wait is incorrect
                // when there was no running controller before the transition began.
                var controllerAvailable = await controller.IsReachableAsync(cancellationToken);
                if (!controllerAvailable.Succeeded)
                {
                    logger?.LogWarning("Mihomo TUN configuration was persisted but not reloaded because its controller was already unavailable. Enabled={Enabled} ProblemCode={ProblemCode}",
                        enabled, controllerAvailable.ProblemCode);
                    return controllerAvailable.ProblemCode;
                }
                var reload = await controller.ReloadAsync(cancellationToken);
                if (string.IsNullOrEmpty(reload))
                {
                    // Mihomo's reload endpoint acknowledges the configuration before the TUN
                    // adapter is necessarily created.  On Windows, accepting that acknowledgement
                    // alone used to report a successful activation even after Wintun logged
                    // "Access is denied".  Require the configured adapter to become active.
                    var tunDeviceName = (settings.Tun ?? ProxyTunSettingsDto.Default).DeviceName;
                    if (enabled && !await devices.WaitForActiveAsync(tunDeviceName, cancellationToken))
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
        catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
        finally { publication.Gate.Release(); }
    }

    /// <summary>Reports the engine's own view of its TUN flag.  A controller that cannot answer
    /// yields an unobserved result rather than a false "disabled", because callers use a negative
    /// answer to discard durable state.</summary>
    public async Task<ProxyTunRuntimeObservation> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var observation = await controller.GetTunEnabledAsync(cancellationToken);
        return observation.Succeeded
            ? new ProxyTunRuntimeObservation(true, observation.Value!)
            : new ProxyTunRuntimeObservation(false, false, observation.ProblemCode);
    }

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
}
