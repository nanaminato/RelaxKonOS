using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.EventAlerts;

namespace RelaxKonOS.Server.Hubs;

/// <summary>Authenticated invalidation channel. It intentionally carries no event evidence.</summary>
[Authorize(Policy = "EventsRead")]
public sealed class EventAlertsHub : Hub<IEventAlertsHubClient>
{
    internal const string GroupName = "event-alerts.readers";

    public Task Subscribe() => Groups.AddToGroupAsync(Context.ConnectionId, GroupName, Context.ConnectionAborted);
    public override Task OnDisconnectedAsync(Exception? exception) => base.OnDisconnectedAsync(exception);
}

/// <summary>Bridges committed store changes to the hub after the SQLite transaction commits.</summary>
public sealed class EventAlertNotificationHub(EventAlertStore store, IHubContext<EventAlertsHub, IEventAlertsHubClient> hub)
{
    public Task BroadcastAsync(AlertChangedDto change) => hub.Clients.Group(EventAlertsHub.GroupName).OnAlertChanged(change);

    public void Attach() => store.AlertChanged += BroadcastAsync;
}
