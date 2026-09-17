using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Hubs;

/// <summary>已认证的系统性能订阅 Hub。首位订阅者启动全局采样，最后一位离开时停止。</summary>
[Authorize]
public sealed class PerformanceHub(PerformanceSubscriptionRegistry subscriptions) : Hub<IPerformanceHubClient>
{
    internal const string GroupName = "system-performance";

    public async Task Subscribe()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName);
        subscriptions.Subscribe(Context.ConnectionId);
    }

    public async Task Unsubscribe()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName);
        subscriptions.Unsubscribe(Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Unsubscribe(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
