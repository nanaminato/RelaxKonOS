using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;

namespace RelaxKonOS.Server.Hubs;

/// <summary>已认证的系统性能订阅 Hub。采样器全局只采样一次，订阅仅控制事件广播。</summary>
[Authorize]
public sealed class PerformanceHub : Hub<IPerformanceHubClient>
{
    internal const string GroupName = "system-performance";

    public Task Subscribe() => Groups.AddToGroupAsync(Context.ConnectionId, GroupName);

    public Task Unsubscribe() => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName);
}
