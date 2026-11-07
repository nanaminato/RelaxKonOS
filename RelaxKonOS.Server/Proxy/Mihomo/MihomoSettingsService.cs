using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Applies the small, safe set of Manager-owned Mihomo options to the protected config.</summary>
public sealed class MihomoSettingsService(
    IProxyPlatformPaths paths,
    IMihomoControllerClient controller,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions,
    Platform.IHostSystemProxyService systemProxyService,
    MihomoPublicationStore publication,
    ILogger<MihomoSettingsService>? logger = null,
    MihomoConfigurationPublicationService? managedPublication = null,
    MihomoRuntimeService? managedRuntime = null,
    MihomoSettingsPublicationFiles? settingsFiles = null) : IProxySettingsWriter
{
    private static readonly HashSet<string> LogLevels = new(StringComparer.OrdinalIgnoreCase) { "silent", "error", "warning", "info", "debug" };
    private SemaphoreSlim _gate => publication.Gate;

    public async Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.MixedPort is < 1 or > 65535 || !LogLevels.Contains(request.LogLevel)) return ProxyProblemCodes.ConfigInvalid;
        if (request.SystemProxyEnabled)
        {
            var capabilities = await systemProxyService.GetCapabilitiesAsync(cancellationToken);
            if (!capabilities.Supported || request.SystemProxy?.UsePac == true && !capabilities.SupportsPac) return ProxyProblemCodes.NotSupported;
        }
        var systemProxyHost = NormalizeSystemProxyHost(request.SystemProxyHost);
        if (systemProxyHost is null) return ProxyProblemCodes.ConfigInvalid;
        var tun = request.Tun ?? ProxyTunSettingsDto.Default;
        if (!IsValidTunSettings(tun)) return ProxyProblemCodes.ConfigInvalid;
        var systemProxy = request.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
        if (!IsValidSystemProxyOptions(systemProxy)) return ProxyProblemCodes.ConfigInvalid;
        var settings = new ProxySettingsDto(request.SystemProxyEnabled, request.AllowLan, request.DnsEnabled, request.Ipv6Enabled,
            request.UnifiedDelay, request.LogLevel.ToLowerInvariant(), request.MixedPort, request.AllowInsecureSubscriptionSources, systemProxyHost, tun, systemProxy);
        await _gate.WaitAsync(cancellationToken);
        MihomoDirectReload? checkpoint = null;
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending is not null) return ProxyProblemCodes.RecoveryRequired;
            var previous = await ReadAsync(cancellationToken);
            if (state.Published is not null)
                return await UpdateManagedUnderGateAsync(settings, previous, state, cancellationToken);
            if (await publication.UncoordinatedWriteProblemAsync(cancellationToken) is { } publicationProblem) return publicationProblem;
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
            var originalMode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(active);
            // A settings write is not a TUN transition.  Carry the live activation forward;
            // letting the managed TUN block fall back to "disabled" tore the adapter down and
            // left the durable marker still reporting an active session.
            var tunActivation = MihomoManagedConfiguration.ReadTunActivation(original);
            string updated;
            try
            {
                updated = MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithServerGeoDataSettings(
                        MihomoManagedConfiguration.WithRuntimeSettings(
                            MihomoManagedConfiguration.WithManagedTunSettings(original, settings,
                                tunActivation.Enabled, tunActivation.RouteExclusions), settings)), controllerOptions,
                    await controllerSecrets.GetOrCreateAsync(cancellationToken));
            }
            catch (ProxyControllerSecretException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }

            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                if (!OperatingSystem.IsWindows() && originalMode is { } mode) File.SetUnixFileMode(temporary, mode);
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint = await publication.BeginDirectReloadAsync(original, updated, cancellationToken);
                File.Move(temporary, active, overwrite: true);
                // The active file is already visible. Finish the update or compensation even if the client leaves.
                var completionToken = CancellationToken.None;
                var controllerAvailable = (await controller.IsReachableAsync(completionToken)).Succeeded;
                var reload = controllerAvailable ? await controller.ReloadAsync(completionToken) : null;
                if (controllerAvailable && !string.IsNullOrEmpty(reload))
                {
                    await RestoreConfigurationAsync();
                    if (reload == ProxyProblemCodes.RecoveryRequired) return reload;
                    if (!string.IsNullOrEmpty(await controller.ReloadAsync(completionToken))) return ProxyProblemCodes.RecoveryRequired;
                    await publication.CompleteDirectReloadAsync(checkpoint.OperationId);
                    return ProxyProblemCodes.ConfigApplyFailed;
                }
                var proxyProblem = await systemProxyService.ApplyAsync(settings, previous, enforce: false, completionToken);
                // An absent Helper must not block unrelated Mihomo settings while the proxy is off.
                // Startup reconciliation still retries, so an interrupted root-side write can recover.
                if (!settings.SystemProxyEnabled && !previous.SystemProxyEnabled
                    && proxyProblem == ProxyProblemCodes.PrivilegedOperationUnavailable) proxyProblem = null;
                if (proxyProblem is not null)
                {
                    logger?.LogWarning("Proxy settings update could not apply the host system proxy. ProblemCode={ProblemCode}", proxyProblem);
                    await RestoreConfigurationAsync();
                    if (controllerAvailable && !string.IsNullOrEmpty(await controller.ReloadAsync(completionToken)))
                        return ProxyProblemCodes.RecoveryRequired;
                    if (proxyProblem != ProxyProblemCodes.SystemProxyConflict) return ProxyProblemCodes.RecoveryRequired;
                    await publication.CompleteDirectReloadAsync(checkpoint.OperationId);
                    return proxyProblem;
                }
                try { await WriteAsync(settings, completionToken); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    var rollback = await systemProxyService.ApplyAsync(previous, settings, enforce: true, CancellationToken.None);
                    await RestoreConfigurationAsync();
                    if (controllerAvailable && !string.IsNullOrEmpty(await controller.ReloadAsync(CancellationToken.None)))
                        return ProxyProblemCodes.RecoveryRequired;
                    if (rollback is null) await publication.CompleteDirectReloadAsync(checkpoint.OperationId);
                    return rollback is null ? ProxyProblemCodes.ConfigApplyFailed : ProxyProblemCodes.RecoveryRequired;
                }
                await publication.CompleteDirectReloadAsync(checkpoint.OperationId);
                logger?.LogInformation("Proxy settings updated. SystemProxyEnabled={SystemProxyEnabled} AllowLan={AllowLan} DnsEnabled={DnsEnabled} MixedPort={MixedPort} TunEnabled={TunEnabled}",
                    settings.SystemProxyEnabled, settings.AllowLan, settings.DnsEnabled, settings.MixedPort, tunActivation.Enabled);
                return null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }

            async Task RestoreConfigurationAsync()
            {
                await File.WriteAllTextAsync(temporary, original, CancellationToken.None);
                if (!OperatingSystem.IsWindows() && originalMode is { } mode) File.SetUnixFileMode(temporary, mode);
                File.Move(temporary, active, overwrite: true);
            }
        }
        catch (Exception error) when (checkpoint is not null && error is (IOException or UnauthorizedAccessException or ArgumentException))
        { return ProxyProblemCodes.RecoveryRequired; }
        catch (RuntimePolicyException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (InvalidDataException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
        catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
        finally { _gate.Release(); }
    }

    private async Task<string?> UpdateManagedUnderGateAsync(ProxySettingsDto settings, ProxySettingsDto previous,
        MihomoPublicationState state, CancellationToken ct)
    {
        if (managedPublication is null || managedRuntime is null || settingsFiles is null) return ProxyProblemCodes.NotSupported;
        try
        {
            var observed = await MihomoManagedPublicationVerification.VerifyAsync(paths, controllerSecrets, managedRuntime, state, ct);
            var candidate = MihomoSettingsPublicationFiles.Candidate(state.Published!, settings);
            if (MihomoConfigurationPolicy.Validate(candidate).Material.NetworkTransitionRequired)
                return ProxyProblemCodes.NotSupported;
            var intent = await settingsFiles.CaptureAsync(previous, settings, ct);
            var operationId = Guid.NewGuid();
            var plan = new RuntimeConfigurationPlan(observed.Installation.InstallationId, observed.Installation.HostId,
                "mihomo", observed.Installation.StateRevision, observed.ActiveRevisionId!.Value, state.Identity!,
                MihomoPublicationStore.Identity(candidate.Yaml), operationId.ToString("N"));
            var result = await managedPublication.PublishUnderGateAsync(operationId, state.Revision, plan, candidate, ct, intent);
            return result.Outcome == MihomoPublicationOutcome.Confirmed ? null
                : result.Outcome == MihomoPublicationOutcome.RecoveryRequired ? ProxyProblemCodes.RecoveryRequired : ProxyProblemCodes.ConfigApplyFailed;
        }
        catch (Exception ex) when (ex is RuntimePolicyException or RuntimeLifecycleException or IOException or InvalidDataException or ProxyControllerSecretException)
        { return ProxyProblemCodes.RecoveryRequired; }
    }

    private Task<ProxySettingsDto> ReadAsync(CancellationToken cancellationToken) =>
        ProxySettingsReader.ReadAsync(paths, cancellationToken);

    private async Task WriteAsync(ProxySettingsDto settings, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
        var path = Path.Combine(directory, "mihomo-settings.json");
        var temporary = path + ".new";
        await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, settings, cancellationToken: cancellationToken);
        File.Move(temporary, path, overwrite: true);
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

    private static bool IsValidDnsHijack(string value)
    {
        var match = Regex.Match(value, "^(?:(?:tcp|udp)://)?(?:any|[0-9A-Fa-f:.]+):(\\d{1,5})$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535;
    }


    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

}
