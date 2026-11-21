using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.SystemMonitor;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerMonitorCapability(
    AppId appId,
    IAppPermissionManager permissions,
    ExternalServerConnection connection,
    ITaskManagerClient systemMonitor) : IServerMonitor
{
    public async Task<ServerMetricsResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetPerformanceInfoAsync(cancellationToken);
        if (info.Data is null) return new(info.Status, null);
        var snapshot = await GetPerformanceSnapshotAsync(cancellationToken);
        if (snapshot.Data is null) return new(snapshot.Status, null);
        try { return new(AppCapabilityResult.Succeeded, ToSnapshot(info.Data, snapshot.Data)); }
        catch (ArgumentException) { return new(AppCapabilityResult.Unavailable, null); }
    }

    public Task<ServerPerformanceResult<PerformanceInfoDto>> GetPerformanceInfoAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(systemMonitor.GetPerformanceInfoAsync, cancellationToken);

    public Task<ServerPerformanceResult<PerformanceRealtimeSnapshotDto>> GetPerformanceSnapshotAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(systemMonitor.GetPerformanceSnapshotAsync, cancellationToken);

    public Task<ServerPerformanceResult<IReadOnlyList<PerformanceRealtimeSnapshotDto>>> GetPerformanceHistoryAsync(
        int seconds = 60, CancellationToken cancellationToken = default) =>
        ReadAsync(token => systemMonitor.GetPerformanceHistoryAsync(Math.Clamp(seconds, 1, 60), token), cancellationToken);

    public IServerPerformanceStream CreatePerformanceStream() => new PerformanceStream(connection.Session, Readiness);

    private AppCapabilityResult Readiness()
    {
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerMetricsRead))
            return AppCapabilityResult.PermissionDenied;
        return connection.IsCurrent ? AppCapabilityResult.Succeeded : AppCapabilityResult.Unavailable;
    }

    private async Task<ServerPerformanceResult<T>> ReadAsync<T>(Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readiness = Readiness();
        if (readiness != AppCapabilityResult.Succeeded) return new(readiness, null);

        try
        {
            var data = await read(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            readiness = Readiness();
            return new(readiness, readiness == AppCapabilityResult.Succeeded ? data : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(AppCapabilityResult.Unavailable, null);
        }
    }

    public async IAsyncEnumerable<ServerMetricsResult> WatchAsync(TimeSpan? interval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cadence = interval ?? TimeSpan.FromSeconds(2);
        cadence = TimeSpan.FromMilliseconds(Math.Clamp(cadence.TotalMilliseconds, 1000, 60000));
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return await GetSnapshotAsync(cancellationToken);
            await Task.Delay(cadence, cancellationToken);
        }
    }

    private static ServerMetricsSnapshot ToSnapshot(PerformanceInfoDto info, PerformanceRealtimeSnapshotDto snapshot)
    {
        var filesystemNames = info.Filesystems.ToDictionary(filesystem => filesystem.Id, filesystem => filesystem.Name, StringComparer.Ordinal);
        var networkNames = info.Networks.ToDictionary(network => network.Id, network => network.Name, StringComparer.Ordinal);
        return new ServerMetricsSnapshot(
            snapshot.Timestamp,
            snapshot.Cpu.TotalPercent,
            info.Cpu.LogicalProcessorCount,
            snapshot.Cpu.PerLogicalCpuPercent,
            snapshot.Memory.TotalBytes,
            snapshot.Memory.UsedBytes,
            snapshot.Memory.AvailableBytes,
            snapshot.Memory.TotalBytes == 0 ? 0 : snapshot.Memory.UsedBytes * 100d / snapshot.Memory.TotalBytes,
            snapshot.Filesystems.Select(filesystem => new ServerDiskMetric(
                filesystemNames.GetValueOrDefault(filesystem.Id, filesystem.Id), filesystem.TotalBytes, filesystem.UsedBytes,
                filesystem.AvailableBytes, filesystem.Percent)).ToArray(),
            snapshot.Networks.Select(network => new ServerNetworkMetric(
                networkNames.GetValueOrDefault(network.Id, network.Id), network.SendBytesPerSecond, network.ReceiveBytesPerSecond)).ToArray(),
            [],
            snapshot.UptimeSeconds);
    }
}
