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
    IHubContext<PerformanceHub, IPerformanceHubClient> hub) : BackgroundService
{
    private readonly Channel<PerformanceRealtimeSnapshotDto> _queue = Channel.CreateBounded<PerformanceRealtimeSnapshotDto>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Enqueue(PerformanceRealtimeSnapshotDto snapshot) => _queue.Writer.TryWrite(snapshot);
        sampler.SnapshotAvailable += Enqueue;
        try
        {
            await foreach (var snapshot in _queue.Reader.ReadAllAsync(stoppingToken))
                await hub.Clients.Group(PerformanceHub.GroupName).OnPerformanceSnapshot(snapshot);
        }
        finally
        {
            sampler.SnapshotAvailable -= Enqueue;
            _queue.Writer.TryComplete();
        }
    }
}
