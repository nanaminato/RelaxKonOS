using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host-wide TUN transaction guard. The marker is durable before a platform network change.</summary>
public sealed class ProxyTunSafetyService(
    IProxyPlatformPaths paths,
    IProxyNetworkSafetyPlatform platform,
    IProxyTunRuntimeController? runtime = null,
    ILogger<ProxyTunSafetyService>? logger = null,
    MihomoPublicationStore? publication = null,
    MihomoRuntimeService? managedRuntime = null) : IProxyTunSafetyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? currentSession;
    private readonly IProxyTunRuntimeController _runtime = runtime ?? new UnavailableProxyTunRuntimeController();
    public async Task<string?> EnableAsync(Guid profileId, System.Net.IPAddress? managementAddress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadMarkerAsync(paths, cancellationToken);
            if (existing is not null && !await DiscardStaleSessionMarkerAsync(existing, cancellationToken))
            {
                logger?.LogWarning("Refused TUN activation because a previous recovery marker is still present. ProfileId={ProfileId}", profileId);
                return ProxyProblemCodes.RecoveryRequired;
            }
            var snapshot = await platform.CaptureManagementRouteAsync(managementAddress, cancellationToken);
            if (snapshot is null || !snapshot.ManagementPathSafe)
            {
                logger?.LogWarning("Refused TUN activation because no safe management route could be captured. ProfileId={ProfileId}", profileId);
                return ProxyProblemCodes.ManagementRouteUnsafe;
            }
            var marker = new RecoveryMarker(Guid.NewGuid(), profileId, snapshot, DateTimeOffset.UtcNow, false);
            var markerPersisted = false;
            var runtimeProblem = await _runtime.SetEnabledAsync(marker.OperationId, snapshot, true, async ct =>
            {
                await WriteMarkerAsync(marker, ct);
                markerPersisted = true;
                logger?.LogInformation("TUN activation marker persisted. OperationId={OperationId} ProfileId={ProfileId} EgressInterface={EgressInterface} ManagementAddressCount={ManagementAddressCount}", marker.OperationId, profileId, snapshot.EgressInterface, snapshot.ManagementAddresses.Count);
            }, cancellationToken);
            if (!markerPersisted) return runtimeProblem ?? ProxyProblemCodes.TunActivationFailed;
            if (!string.IsNullOrEmpty(runtimeProblem)) return await RestoreAndReportAsync(marker, cancellationToken, runtimeProblem);
            if (!await platform.ApplyTunAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.TunActivationFailed);
            if (!await platform.VerifyManagementRouteAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.ManagementRouteUnsafe);
            // Before this point the marker means an interrupted transition must be recovered.
            // Once the management path is verified it instead represents a live TUN session
            // that must be torn down if the Server restarts.  Keeping those two meanings
            // distinct lets the overview report the active mode without masking recovery.
            await WriteMarkerAsync(marker with { ActivationCompleted = true }, cancellationToken);
            currentSession = marker.OperationId;
            logger?.LogInformation("TUN activation completed and the protected management route is reachable. OperationId={OperationId} ProfileId={ProfileId}", marker.OperationId, profileId);
            return null;
        }
        finally { _gate.Release(); }
    }
    public Task<string?> DisableAsync(CancellationToken cancellationToken) => EmergencyDisableAsync(cancellationToken);
    public async Task<string?> EmergencyDisableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var marker = await ReadMarkerAsync(paths, cancellationToken);
            if (marker is null) return null;
            return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.RecoveryFailed);
        }
        finally { _gate.Release(); }
    }
    public async Task<ProxyTunRecoveryReference?> CaptureRecoveryAsync(bool includeCurrentSession, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var marker = await ReadMarkerAsync(paths, cancellationToken);
            return marker is null || marker.ActivationCompleted && marker.OperationId == currentSession && !includeCurrentSession
                ? null : new(marker.OperationId, marker.CreatedAt);
        }
        finally { _gate.Release(); }
    }
    public async Task<string?> RecoverAsync(ProxyTunRecoveryReference reference, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var marker = await ReadMarkerAsync(paths, cancellationToken);
            if (marker is null) return null;
            if (marker.OperationId == Guid.Empty) return ProxyProblemCodes.RecoveryRequired;
            if (marker.OperationId != reference.OperationId || marker.CreatedAt != reference.CreatedAt) return null;
            return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.RecoveryFailed);
        }
        finally { _gate.Release(); }
    }
    public async Task<ProxyRecoveryStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadMarkerAsync(paths, cancellationToken);
        if (marker is not null)
            return marker.ActivationCompleted
                ? new(false, true, marker.CreatedAt)
                : new(true, true, marker.CreatedAt, ProxyProblemCodes.RecoveryRequired);
        if (publication is null || managedRuntime is null) return new(false, false, null);
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending?.TunIntent is not null) return new(true, false, null, ProxyProblemCodes.RecoveryRequired);
            if (state.Published?.NetworkTransitionRequired != true) return new(false, false, null);
            var observed = await managedRuntime.QueryHelperAsync(cancellationToken);
            if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired
                || observed.MihomoTunPublicationId is null || observed.MihomoTunRunning is null || observed.ProblemCode is not null)
                return new(true, false, null, ProxyProblemCodes.RecoveryRequired);
            return new(false, false, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RuntimePolicyException or RuntimeLifecycleException)
        { return new(true, false, null, ProxyProblemCodes.RecoveryRequired); }
    }
    /// <summary>
    /// A completed marker asserts that a TUN session is live.  When the engine itself reports TUN
    /// as off, that assertion is stale: the runtime was reconfigured outside this transaction, and
    /// honouring the marker would refuse every later activation forever.  An unfinished marker is
    /// never stale - it still describes an interrupted transition that must be recovered.
    /// </summary>
    private async Task<bool> DiscardStaleSessionMarkerAsync(RecoveryMarker marker, CancellationToken cancellationToken)
    {
        if (!marker.ActivationCompleted) return false;
        var observation = await _runtime.IsEnabledAsync(cancellationToken);
        // Unobserved is not the same as disabled: only a definite negative answer may clear state.
        if (!observation.Succeeded || observation.Enabled) return false;
        logger?.LogWarning("A completed TUN session marker contradicts the engine, which reports TUN as disabled. Discarding it. OperationId={OperationId} ProfileId={ProfileId}",
            marker.OperationId, marker.ProfileId);
        // Reuse the ordinary teardown so the platform can still confirm that the captured
        // management path won before another activation is allowed to change the network.
        return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.RecoveryFailed) is null;
    }
    private async Task<string?> RestoreAndReportAsync(RecoveryMarker marker, CancellationToken cancellationToken, string failedCode)
    {
        if (marker.OperationId == Guid.Empty) return ProxyProblemCodes.RecoveryRequired;

        // A live session becomes unfinished before teardown can touch the runtime or routes.
        cancellationToken.ThrowIfCancellationRequested();
        if (marker.ActivationCompleted)
            await WriteMarkerAsync(marker with { ActivationCompleted = false }, cancellationToken);

        // Disable Mihomo's TUN first; this is the component that owns routes and DNS.  We still
        // call the platform restore hook so it can verify that the original management path won.
        var runtimeProblem = await _runtime.SetEnabledAsync(marker.OperationId, marker.Snapshot, false, null, CancellationToken.None);
        // Once dispatched, keep the same operation through route verification and marker cleanup.
        var restored = await platform.RestoreAsync(marker.Snapshot, CancellationToken.None);
        if (!restored || !string.IsNullOrEmpty(runtimeProblem))
        {
            logger?.LogError("TUN recovery could not restore the protected management path. OperationId={OperationId} PlatformRestored={PlatformRestored} RuntimeProblemCode={RuntimeProblemCode}",
                marker.OperationId, restored, runtimeProblem ?? "");
            return ProxyProblemCodes.RecoveryRequired;
        }
        DeleteMarker();
        currentSession = null;
        logger?.LogWarning("TUN was disabled and the protected management route was restored. OperationId={OperationId} Cause={Cause}", marker.OperationId, failedCode);
        return failedCode == ProxyProblemCodes.RecoveryFailed ? null : failedCode;
    }
    private static async Task<RecoveryMarker?> ReadMarkerAsync(IProxyPlatformPaths paths, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.GetStateDirectory(), "proxy-tun-recovery.json");
        RelaxKonOS.ManagedRuntimes.RuntimeSourceStore.RequireNoLinks(path);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is 0 or > 65536) return UnknownMarker();
            var marker = await JsonSerializer.DeserializeAsync<RecoveryMarker>(input, cancellationToken: cancellationToken);
            return marker is not null && marker.OperationId != Guid.Empty && marker.CreatedAt != default && marker.ProfileId != Guid.Empty
                && marker.Snapshot is { ManagementPathSafe: true } snapshot
                && !string.IsNullOrWhiteSpace(snapshot.EgressInterface) && System.Net.IPAddress.TryParse(snapshot.DefaultGateway, out _)
                && marker.Snapshot.SystemBypass is not null && marker.Snapshot.ManagementAddresses is not null ? marker : UnknownMarker();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return UnknownMarker(); }
    }
    internal static async Task<bool> HasUnknownRecoveryAsync(IProxyPlatformPaths paths, CancellationToken ct)
    {
        var marker = await ReadMarkerAsync(paths, ct);
        return marker is not null && marker.OperationId == Guid.Empty;
    }
    internal static async Task<bool> HasRecoveryMarkerAsync(IProxyPlatformPaths paths, CancellationToken ct) =>
        await ReadMarkerAsync(paths, ct) is not null;
    private static RecoveryMarker UnknownMarker() => new(Guid.Empty, Guid.Empty, new("corrupt", default, false, "", "", [], []), default, false);
    private async Task WriteMarkerAsync(RecoveryMarker marker, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = MarkerPath() + ".new";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(output, marker, cancellationToken: cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, MarkerPath(), overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                LinuxDirectorySync.Flush(directory);
                LinuxDirectorySync.Flush(Path.GetDirectoryName(directory)!);
            }
        }
        finally { File.Delete(temporary); }
    }
    private void DeleteMarker()
    {
        File.Delete(MarkerPath());
        if (OperatingSystem.IsLinux()) LinuxDirectorySync.Flush(paths.GetStateDirectory());
    }
    private string MarkerPath() => Path.Combine(paths.GetStateDirectory(), "proxy-tun-recovery.json");
    private sealed record RecoveryMarker(Guid OperationId, Guid ProfileId, ProxyManagementRouteSnapshot Snapshot, DateTimeOffset CreatedAt, bool ActivationCompleted);
}
