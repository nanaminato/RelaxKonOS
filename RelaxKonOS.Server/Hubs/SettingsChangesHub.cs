using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Hubs;

public sealed class SettingsSubscriptions
{
    internal ConcurrentDictionary<string, Subscription> Connections { get; } = new();
    internal sealed record Subscription(Guid UserId, Guid WorkspaceId);
}

[Authorize]
public sealed class SettingsChangesHub(IWorkspaceRepository workspaces, SettingsSubscriptions subscriptions)
    : Hub<ISettingsChangesClient>
{
    public Task Subscribe(Guid workspaceId)
    {
        var subject = Context.User?.FindFirst("sub")?.Value ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId) || workspaces.FindById(workspaceId) is not { } workspace
            || workspace.UserId != userId)
            throw new HubException("settings.workspace_unavailable");
        subscriptions.Connections[Context.ConnectionId] = new(userId, workspaceId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Connections.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Observes all registry writers, including the registry editor, without broadcasting values.</summary>
public sealed class SettingsChangesBroadcastService(
    SettingsSubscriptions subscriptions, IServiceScopeFactory scopeFactory,
    IHubContext<SettingsChangesHub, ISettingsChangesClient> hub, ILogger<SettingsChangesBroadcastService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delivered = new Dictionary<string, WorkspaceSettingsChanged>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var key in delivered.Keys.Where(key => !subscriptions.Connections.ContainsKey(key)).ToArray())
                delivered.Remove(key);
            foreach (var group in subscriptions.Connections.ToArray().GroupBy(pair => pair.Value))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
                    var settings = scope.ServiceProvider.GetRequiredService<IWorkspaceSettingsService>();
                    var workspace = workspaces.FindById(group.Key.WorkspaceId);
                    if (workspace is null || workspace.UserId != group.Key.UserId) continue;
                    var snapshot = settings.Read(workspace);
                    var change = new WorkspaceSettingsChanged(workspace.Id, snapshot.Revision!.Value, snapshot.PersistedRevision);
                    foreach (var (connectionId, subscription) in group)
                    {
                        if (!subscriptions.Connections.TryGetValue(connectionId, out var current) || current != subscription) continue;
                        if (delivered.TryGetValue(connectionId, out var previous) && previous == change) continue;
                        await hub.Clients.Client(connectionId).OnWorkspaceSettingsChanged(change);
                        delivered[connectionId] = change;
                    }
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Do not log snapshot values or exception bodies which may contain persisted data.
                    logger.LogWarning("Settings invalidation delivery failed; retrying on the next observation.");
                }
            }
        }
    }
}
