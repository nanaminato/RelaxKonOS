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
    public async Task<string?> SetEnabledAsync(RelaxKonOS.Server.Proxy.Platform.ProxyManagementRouteSnapshot snapshot, bool enabled,
        Func<CancellationToken, Task>? beforeActivation, CancellationToken cancellationToken)
    {
        await publication.Gate.WaitAsync(cancellationToken);
        try
        {
            var authority = await publication.ReadAsync(cancellationToken);
            if (authority.Pending is not null || enabled && authority.DirectReload is not null) return ProxyProblemCodes.RecoveryRequired;
            if (authority.Published is not null) return ProxyProblemCodes.NotSupported;
            var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
            if (!File.Exists(active)) return ProxyProblemCodes.RuntimeNotInstalled;
            var settings = await settingsReader.GetAsync(cancellationToken);
            var deviceName = (settings.Tun ?? ProxyTunSettingsDto.Default).DeviceName;
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            var replaceUnconfirmed = false;
            if (authority.DirectReload is { } direct)
            {
                // TUN teardown may remove the safety state, but must never reload the unknown candidate.
                if (direct.PreviousYaml is not { } previous) return ProxyProblemCodes.RecoveryRequired;
                var disabled = MihomoManagedConfiguration.WithManagedTunSettings(previous, settings, false, []);
                replaceUnconfirmed = MihomoPublicationStore.Identity(original) == direct.CandidateIdentity;
                if (replaceUnconfirmed) original = previous;
                else if (original != previous && original != disabled) return ProxyProblemCodes.RecoveryRequired;
            }
            var alreadyDisabled = false;
            if (!enabled && (authority.DirectReload is not null || !MihomoManagedConfiguration.ReadTunActivation(original).Enabled))
            {
                var observed = await controller.GetTunEnabledAsync(cancellationToken);
                if (!observed.Succeeded) return observed.ProblemCode;
                alreadyDisabled = !observed.Value;
                if (alreadyDisabled && !replaceUnconfirmed && !MihomoManagedConfiguration.ReadTunActivation(original).Enabled)
                    return await devices.WaitForInactiveAsync(deviceName, cancellationToken) ? null : ProxyProblemCodes.RecoveryRequired;
            }

            var exclusions = enabled ? BuildTunRouteExclusions(snapshot) : [];
            var updated = MihomoManagedConfiguration.WithManagedTunSettings(original, settings, enabled, exclusions);
            if (enabled)
            {
                ArgumentNullException.ThrowIfNull(beforeActivation);
                await beforeActivation(cancellationToken);
            }
            var temporary = active + ".tun-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                SetPrivateFile(temporary);
                File.Move(temporary, active, overwrite: true);
                if (alreadyDisabled)
                    return await devices.WaitForInactiveAsync(deviceName, cancellationToken) ? null : ProxyProblemCodes.RecoveryRequired;
                // A recovery transition can run while Mihomo is intentionally stopped (for
                // example after the Windows Server process was restarted). Do not send a reload
                // to a controller that was already unavailable.
                var controllerAvailable = await controller.IsReachableAsync(cancellationToken);
                if (!controllerAvailable.Succeeded)
                {
                    logger?.LogWarning("Mihomo TUN configuration was persisted but not reloaded because its controller was already unavailable. Enabled={Enabled} ProblemCode={ProblemCode}",
                        enabled, controllerAvailable.ProblemCode);
                    return controllerAvailable.ProblemCode;
                }
                var reload = await controller.ReloadAsync(cancellationToken);
                if (string.IsNullOrEmpty(reload) || reload == ProxyProblemCodes.RecoveryRequired)
                {
                    var applied = reload is null ? await controller.GetTunEnabledAsync(cancellationToken)
                        : await ObserveInterruptedReloadAsync(cancellationToken);
                    if (!applied.Succeeded || applied.Value != enabled)
                        return ProxyProblemCodes.RecoveryRequired;
                    // Mihomo's reload endpoint acknowledges the configuration before the TUN
                    // adapter is necessarily created.  On Windows, accepting that acknowledgement
                    // alone used to report a successful activation even after Wintun logged
                    // "Access is denied".  Require the configured adapter to become active.
                    if (!enabled && !await devices.WaitForInactiveAsync(deviceName, cancellationToken))
                        return ProxyProblemCodes.RecoveryRequired;
                    if (enabled && !await devices.WaitForActiveAsync(deviceName, cancellationToken))
                    {
                        await File.WriteAllTextAsync(active, original, cancellationToken);
                        SetPrivateFile(active);
                        await controller.ReloadAsync(CancellationToken.None);
                        logger?.LogWarning("Mihomo TUN configuration transition was rolled back because the Windows TUN adapter did not become active. DeviceName={DeviceName}", deviceName);
                        return ProxyProblemCodes.TunActivationFailed;
                    }
                    logger?.LogInformation("Mihomo TUN configuration transition completed. Enabled={Enabled} EgressInterface={EgressInterface} ExclusionCount={ExclusionCount}",
                        enabled, snapshot.EgressInterface, exclusions.Count);
                    return null;
                }

                // A lost response is not evidence that the candidate failed. Preserve it and
                // let the durable TUN marker drive recovery against observed controller state.
                if (reload == ProxyProblemCodes.ControllerUnavailable) return ProxyProblemCodes.RecoveryRequired;

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
        catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException) { return ProxyProblemCodes.RecoveryRequired; }
        finally { publication.Gate.Release(); }
    }

    private async Task<ControllerResult<bool>> ObserveInterruptedReloadAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var observed = await controller.GetTunEnabledAsync(budget.Token);
                if (observed.ProblemCode is not (ProxyProblemCodes.ControllerUnavailable or ProxyProblemCodes.ControllerTimeout)) return observed;
                await Task.Delay(TimeSpan.FromMilliseconds(200), budget.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ControllerResult<bool>.Failure(ProxyProblemCodes.RecoveryRequired);
        }
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
