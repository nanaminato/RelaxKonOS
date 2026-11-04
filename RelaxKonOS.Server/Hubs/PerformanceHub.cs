using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Hubs;

/// <summary>已认证的系统性能订阅 Hub。首位订阅者启动全局采样，最后一位离开时停止。</summary>
[Authorize]
public sealed class PerformanceHub(PerformanceHubConnections connections) : Hub<IPerformanceHubClient>
{
    internal const string GroupName = "system-performance";

    public Task Subscribe() => connections.SubscribeAsync(Context.ConnectionId);

    public Task Unsubscribe() => connections.UnsubscribeAsync(Context.ConnectionId);

    public override async Task OnConnectedAsync()
    {
        await connections.ConnectAsync(Context.ConnectionId, Groups, Context.Abort, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try { await connections.DisconnectAsync(Context.ConnectionId); }
        finally { await base.OnDisconnectedAsync(exception); }
    }
}
