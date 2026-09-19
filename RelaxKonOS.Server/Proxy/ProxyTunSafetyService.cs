using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host-wide TUN transaction guard. The marker is durable before a platform network change.</summary>
public sealed class ProxyTunSafetyService(
    IProxyPlatformPaths paths,
    IProxyNetworkSafetyPlatform platform,
    IProxyTunRuntimeController? runtime = null,
    ILogger<ProxyTunSafetyService>? logger = null) : IProxyTunSafetyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProxyTunRuntimeController _runtime = runtime ?? new UnavailableProxyTunRuntimeController();
    public async Task<string?> EnableAsync(Guid profileId, System.Net.IPAddress? managementAddress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadMarkerAsync(cancellationToken);
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
            await WriteMarkerAsync(marker, cancellationToken);
            logger?.LogInformation("TUN activation marker persisted. OperationId={OperationId} ProfileId={ProfileId} EgressInterface={EgressInterface} ManagementAddressCount={ManagementAddressCount}", marker.OperationId, profileId, snapshot.EgressInterface, snapshot.ManagementAddresses.Count);
            var runtimeProblem = await _runtime.SetEnabledAsync(snapshot, true, cancellationToken);
            if (!string.IsNullOrEmpty(runtimeProblem)) return await RestoreAndReportAsync(marker, cancellationToken, runtimeProblem);
            if (!await platform.ApplyTunAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.TunActivationFailed);
            if (!await platform.VerifyManagementRouteAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.ManagementRouteUnsafe);
            // Before this point the marker means an interrupted transition must be recovered.
            // Once the management path is verified it instead represents a live TUN session
            // that must be torn down if the Server restarts.  Keeping those two meanings
            // distinct lets the overview report the active mode without masking recovery.
            await WriteMarkerAsync(marker with { ActivationCompleted = true }, cancellationToken);
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
            var marker = await ReadMarkerAsync(cancellationToken);
            if (marker is null) return null;
            return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.RecoveryFailed);
        }
        finally { _gate.Release(); }
    }
    public async Task<string?> EvaluateRecoveryAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        return marker is null ? null : await EmergencyDisableAsync(cancellationToken);
    }
    public async Task<ProxyRecoveryStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        return marker is null
            ? new(false, false, null)
            : marker.ActivationCompleted
                ? new(false, true, marker.CreatedAt)
                : new(true, true, marker.CreatedAt, ProxyProblemCodes.RecoveryRequired);
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
        // Disable Mihomo's TUN first; this is the component that owns routes and DNS.  We still
        // call the platform restore hook so it can verify that the original management path won.
        var runtimeProblem = await _runtime.SetEnabledAsync(marker.Snapshot, false, CancellationToken.None);
        var restored = await platform.RestoreAsync(marker.Snapshot, cancellationToken);
        if (!restored || !string.IsNullOrEmpty(runtimeProblem))
        {
            logger?.LogError("TUN recovery could not restore the protected management path. OperationId={OperationId} PlatformRestored={PlatformRestored} RuntimeProblemCode={RuntimeProblemCode}",
                marker.OperationId, restored, runtimeProblem ?? "");
            return ProxyProblemCodes.RecoveryRequired;
        }
        DeleteMarker();
        logger?.LogWarning("TUN was disabled and the protected management route was restored. OperationId={OperationId} Cause={Cause}", marker.OperationId, failedCode);
        return failedCode == ProxyProblemCodes.RecoveryFailed ? null : failedCode;
    }
    private async Task<RecoveryMarker?> ReadMarkerAsync(CancellationToken cancellationToken)
    {
        var path = MarkerPath(); if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RecoveryMarker>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return new RecoveryMarker(Guid.Empty, Guid.Empty, new("corrupt", DateTimeOffset.UtcNow, false, "", "", [], []), DateTimeOffset.UtcNow, false); }
    }
    private async Task WriteMarkerAsync(RecoveryMarker marker, CancellationToken cancellationToken)
    {
        var dir = paths.GetStateDirectory(); Directory.CreateDirectory(dir); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = MarkerPath() + ".new"; await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, marker, cancellationToken: cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.Move(temporary, MarkerPath(), overwrite: true);
    }
    private void DeleteMarker() { if (File.Exists(MarkerPath())) File.Delete(MarkerPath()); }
    private string MarkerPath() => Path.Combine(paths.GetStateDirectory(), "proxy-tun-recovery.json");
    private sealed record RecoveryMarker(Guid OperationId, Guid ProfileId, ProxyManagementRouteSnapshot Snapshot, DateTimeOffset CreatedAt, bool ActivationCompleted);
}
