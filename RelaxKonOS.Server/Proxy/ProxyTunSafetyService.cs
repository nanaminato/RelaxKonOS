using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host-wide TUN transaction guard. The marker is durable before a platform network change.</summary>
public sealed class ProxyTunSafetyService(IProxyPlatformPaths paths, IProxyNetworkSafetyPlatform platform) : IProxyTunSafetyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string?> EnableAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ReadMarkerAsync(cancellationToken) is not null) return ProxyProblemCodes.RecoveryRequired;
            var snapshot = await platform.CaptureManagementRouteAsync(cancellationToken);
            if (snapshot is null || !snapshot.ManagementPathSafe) return ProxyProblemCodes.ManagementRouteUnsafe;
            var marker = new RecoveryMarker(Guid.NewGuid(), profileId, snapshot, DateTimeOffset.UtcNow);
            await WriteMarkerAsync(marker, cancellationToken);
            if (!await platform.ApplyTunAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.TunActivationFailed);
            if (!await platform.VerifyManagementRouteAsync(snapshot, cancellationToken)) return await RestoreAndReportAsync(marker, cancellationToken, ProxyProblemCodes.ManagementRouteUnsafe);
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
        return marker is null ? new(false, false, null) : new(true, true, marker.CreatedAt, ProxyProblemCodes.RecoveryRequired);
    }
    private async Task<string?> RestoreAndReportAsync(RecoveryMarker marker, CancellationToken cancellationToken, string failedCode)
    {
        if (!await platform.RestoreAsync(marker.Snapshot, cancellationToken)) return ProxyProblemCodes.RecoveryRequired;
        DeleteMarker(); return failedCode == ProxyProblemCodes.RecoveryFailed ? null : failedCode;
    }
    private async Task<RecoveryMarker?> ReadMarkerAsync(CancellationToken cancellationToken)
    {
        var path = MarkerPath(); if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RecoveryMarker>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return new RecoveryMarker(Guid.Empty, Guid.Empty, new("corrupt", DateTimeOffset.UtcNow, false, "", "", []), DateTimeOffset.UtcNow); }
    }
    private async Task WriteMarkerAsync(RecoveryMarker marker, CancellationToken cancellationToken)
    {
        var dir = paths.GetStateDirectory(); Directory.CreateDirectory(dir); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = MarkerPath() + ".new"; await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, marker, cancellationToken: cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.Move(temporary, MarkerPath(), overwrite: true);
    }
    private void DeleteMarker() { if (File.Exists(MarkerPath())) File.Delete(MarkerPath()); }
    private string MarkerPath() => Path.Combine(paths.GetStateDirectory(), "proxy-tun-recovery.json");
    private sealed record RecoveryMarker(Guid OperationId, Guid ProfileId, ProxyManagementRouteSnapshot Snapshot, DateTimeOffset CreatedAt);
}
