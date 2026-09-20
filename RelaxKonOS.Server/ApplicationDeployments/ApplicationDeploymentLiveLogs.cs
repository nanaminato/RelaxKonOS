using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Bounded, sanitized tails. Producers never wait for a browser or a network write.</summary>
internal sealed class ApplicationDeploymentLiveLogs
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Tail> tails = [];
    internal const int MaximumLines = 300;

    public void Append(Guid id, string message, DeploymentStage? stage = null)
    {
        var clean = ApplicationDeploymentLogSanitizer.Sanitize(message);
        if (clean.Length == 0 && stage is null) return;
        lock (gate)
        {
            if (!tails.TryGetValue(id, out var tail))
            {
                // At most four operations normally run concurrently; retain recent finished tails.
                foreach (var stale in tails.Where(x => x.Value.Completed).OrderBy(x => x.Value.Updated)
                             .Take(Math.Max(0, tails.Count - 63)).Select(x => x.Key).ToArray())
                    tails.Remove(stale);
                tails[id] = tail = new();
            }
            tail.Lines.Enqueue(new(DateTimeOffset.UtcNow, clean, stage));
            tail.Version++;
            tail.Updated = DateTimeOffset.UtcNow;
            if (tail.Lines.Count > MaximumLines) { tail.Lines.Dequeue(); tail.Truncated = true; }
        }
    }

    public void Complete(Guid id)
    {
        lock (gate) if (tails.TryGetValue(id, out var tail)) tail.Completed = true;
    }

    public DeploymentLiveLogSnapshot Snapshot(Guid id)
    {
        lock (gate) return tails.TryGetValue(id, out var tail)
            ? new(id, tail.Version, tail.Lines.ToArray(), tail.Truncated)
            : new(id, 0, [], false);
    }

    private sealed class Tail
    {
        public Queue<DeploymentLiveLogLine> Lines { get; } = new();
        public long Version;
        public bool Truncated;
        public bool Completed;
        public DateTimeOffset Updated = DateTimeOffset.UtcNow;
    }
}

internal sealed class ApplicationDeploymentLogSubscriptions
{
    public ConcurrentDictionary<string, Guid> Connections { get; } = new();
}

[Authorize(Policy = ApplicationDeploymentEndpoints.ReadPolicy)]
internal sealed class ApplicationDeploymentLogsHub(ApplicationDeploymentOperationStore operations,
    ApplicationDeploymentLiveLogs logs, ApplicationDeploymentLogSubscriptions subscriptions, IServerModeResolver mode)
    : Hub<IApplicationDeploymentLogsClient>
{
    public Task<DeploymentLiveLogSnapshot> Subscribe(Guid operationId)
    {
        if (!mode.Supports(ServerHostFeature.ApplicationDeployments)) throw new HubException("privileged-feature-unavailable");
        // Same read permission and operation visibility as the REST diagnostics endpoint.
        if (operations.Get(operationId) is null)
            throw new HubException(ApplicationDeploymentProblemCodes.OperationNotFound);
        subscriptions.Connections[Context.ConnectionId] = operationId;
        return Task.FromResult(logs.Snapshot(operationId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Connections.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Coalesces noisy Docker output and sends only changed tails, at most twice per second.</summary>
internal sealed class ApplicationDeploymentLogBroadcastService(ApplicationDeploymentLiveLogs logs,
    ApplicationDeploymentLogSubscriptions subscriptions,
    IHubContext<ApplicationDeploymentLogsHub, IApplicationDeploymentLogsClient> hub,
    ILogger<ApplicationDeploymentLogBroadcastService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delivered = new Dictionary<string, (Guid Id, long Version)>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var stale in delivered.Keys.Where(x => !subscriptions.Connections.ContainsKey(x)).ToArray())
                delivered.Remove(stale);
            foreach (var (connection, id) in subscriptions.Connections.ToArray())
            {
                var snapshot = logs.Snapshot(id);
                if (delivered.TryGetValue(connection, out var prior) && prior == (id, snapshot.Version)) continue;
                try
                {
                    await hub.Clients.Client(connection).OnDeploymentLogs(snapshot).WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
                    delivered[connection] = (id, snapshot.Version);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug("Deployment log delivery deferred. OperationId={OperationId}", id);
                }
            }
        }
    }
}
