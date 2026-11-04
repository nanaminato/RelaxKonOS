using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Hubs;

/// <summary>
/// 将采样事件异步转发给订阅者。有限队列丢弃过时快照，确保慢网络客户端不会阻塞采样循环。
/// </summary>
public sealed class PerformanceBroadcastService(
    IPerformanceSampler sampler,
    IHubContext<PerformanceHub, IPerformanceHubClient> hub,
    ISystemPerformanceWorkLeaseSource workLeases) : BackgroundService
{
    private int _accepting = 1;
    private readonly Channel<PerformanceRealtimeSnapshotDto> _queue = Channel.CreateBounded<PerformanceRealtimeSnapshotDto>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Enqueue(PerformanceRealtimeSnapshotDto snapshot)
        {
            if (Volatile.Read(ref _accepting) != 0) _queue.Writer.TryWrite(snapshot);
        }
        sampler.SnapshotAvailable += Enqueue;
        try
        {
            await foreach (var snapshot in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var work = await workLeases.AcquireAsync(stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
                if (work.Stopping.IsCancellationRequested) return;
                // Typed SignalR sends have no cancellation argument: retain ownership until the actual Task ends.
                await hub.Clients.Group(PerformanceHub.GroupName).OnPerformanceSnapshot(snapshot);
                if (work.Stopping.IsCancellationRequested) return;
            }
        }
        catch (RelaxKonOS.Server.Residency.ModuleUnavailableException error) when (error.Code == "module-host-stopping") { }
        finally
        {
            Volatile.Write(ref _accepting, 0);
            sampler.SnapshotAvailable -= Enqueue;
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _accepting, 0);
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Volatile.Write(ref _accepting, 0);
        base.Dispose();
    }
}
