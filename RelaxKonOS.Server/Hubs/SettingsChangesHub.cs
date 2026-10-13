using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Hubs;

public sealed class SettingsSubscriptions
{
    internal ConcurrentDictionary<string, Subscription> Connections { get; } = new();
    internal ConcurrentDictionary<string, byte> Initial { get; } = new();
    internal Channel<byte> Wake { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    internal sealed record Subscription(Guid UserId, Guid WorkspaceId, Guid? DeviceId);
    internal void Signal() => Wake.Writer.TryWrite(0);
}

[Authorize]
public sealed class SettingsChangesHub(IWorkspaceRepository workspaces, SettingsSubscriptions subscriptions)
    : Hub<ISettingsChangesClient>
{
    public Task Subscribe(Guid workspaceId)
    {
        var subject = Context.User?.FindFirst("sub")?.Value ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId) || workspaces.FindById(workspaceId) is not { } workspace || workspace.UserId != userId)
            throw new HubException("settings.workspace_unavailable");
        var device = Guid.TryParse(Context.User?.FindFirst("device_id")?.Value, out var id) ? id : (Guid?)null;
        subscriptions.Connections[Context.ConnectionId] = new(userId, workspaceId, device);
        subscriptions.Initial[Context.ConnectionId] = 0;
        subscriptions.Signal();
        return Task.CompletedTask;
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Connections.TryRemove(Context.ConnectionId, out _);
        subscriptions.Initial.TryRemove(Context.ConnectionId, out _);
        subscriptions.Signal();
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Bounded wake-ups and revision-only invalidations; idle subscriptions do not scan or parse settings.</summary>
public sealed class SettingsChangesBroadcastService(SettingsSubscriptions subscriptions, IRegistryRepository registry,
    ServerRuntimeSettingsStore runtime, IHubContext<SettingsChangesHub, ISettingsChangesClient> hub,
    ILogger<SettingsChangesBroadcastService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<(Guid, RegistryScope, Guid, string), RegistrySettingsChanged> _pending = new();
    private readonly Dictionary<(string, RegistryScope, Guid, string), RegistrySettingsChanged> _delivered = new();
    private static bool Matches(SettingsSubscriptions.Subscription sub, RegistrySettingsChanged change) => sub.UserId == change.UserId
        && (change.Scope == RegistryScope.Workspace && sub.WorkspaceId == change.ScopeId || change.Scope == RegistryScope.Device && sub.DeviceId == change.ScopeId);
    private void OnChanged(RegistrySettingsChanged change)
    {
        if (RegistrySchema.Find(change.Scope, change.Path, "(Default)") is null || !subscriptions.Connections.Values.Any(sub => Matches(sub, change))) return;
        _pending.AddOrUpdate((change.UserId, change.Scope, change.ScopeId, change.Path), change,
            (_, previous) => change.Revision >= previous.Revision ? change : previous);
        subscriptions.Signal();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.Changed += OnChanged;
        try
        {
            while (await subscriptions.Wake.Reader.WaitToReadAsync(stoppingToken))
            {
                while (subscriptions.Wake.Reader.TryRead(out _)) { }
                await Task.Delay(runtime.Current.Settings.NotificationCoalesceMilliseconds, stoppingToken);
                foreach (var key in _delivered.Keys.Where(key => !subscriptions.Connections.ContainsKey(key.Item1)).ToArray()) _delivered.Remove(key);
                foreach (var id in subscriptions.Initial.Keys.ToArray())
                {
                    if (!subscriptions.Initial.TryRemove(id, out _) || !subscriptions.Connections.TryGetValue(id, out var sub)) continue;
                    foreach (var definition in RegistrySchema.Definitions)
                    {
                        var scopeId = definition.Scope == RegistryScope.Workspace ? sub.WorkspaceId : sub.DeviceId;
                        if (scopeId is not { } scope) continue;
                        var entry = registry.Find(sub.UserId, definition.Scope, scope, definition.Path, definition.Name);
                        if (entry is not null)
                            await DeliverAsync(id, sub, new(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Revision, entry.AppliedRevision), stoppingToken);
                    }
                }
                foreach (var key in _pending.Keys.ToArray())
                {
                    if (!_pending.TryRemove(key, out var change)) continue;
                    foreach (var pair in subscriptions.Connections.ToArray())
                        if (Matches(pair.Value, change)) await DeliverAsync(pair.Key, pair.Value, change, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { registry.Changed -= OnChanged; }
    }
    private async Task DeliverAsync(string id, SettingsSubscriptions.Subscription subscription, RegistrySettingsChanged change, CancellationToken cancellationToken)
    {
        if (!subscriptions.Connections.TryGetValue(id, out var current) || current != subscription) return;
        var key = (id, change.Scope, change.ScopeId, change.Path);
        if (_delivered.TryGetValue(key, out var previous) && (previous.Revision > change.Revision || previous == change || previous.Revision == change.Revision && previous.PersistedRevision is { } persisted && persisted >= (change.PersistedRevision ?? -1))) return;
        try
        {
            if (change.Scope == RegistryScope.Workspace && change.Path == WorkspaceConfigurationRegistry.DesktopPath)
                await hub.Clients.Client(id).OnWorkspaceSettingsChanged(new(change.ScopeId, change.Revision, change.PersistedRevision));
            else await hub.Clients.Client(id).OnRegistrySettingsChanged(change);
            _delivered[key] = change;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Settings invalidation delivery failed; scheduling a bounded retry.");
            OnChanged(change);
            await Task.Delay(TimeSpan.FromSeconds(runtime.Current.Settings.RetryDelaySeconds), cancellationToken);
        }
    }
}
