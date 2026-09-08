using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Server.ProcessGuardian;

namespace RelaxKonOS.Server.Hubs;

/// <summary>Relays changed log snapshots from the isolated Guardian Agent to active SignalR viewers.</summary>
public sealed class GuardianLogBroadcastService(
    IProcessGuardianService guardian,
    GuardianLogSubscriptionRegistry subscriptions,
    IHubContext<GuardianLogsHub, IGuardianLogsHubClient> hub) : BackgroundService
{
    private readonly ConcurrentDictionary<string, string> _lastSnapshots = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var subscribedIds = subscriptions.WorkloadIds;
            foreach (var staleId in _lastSnapshots.Keys.Except(subscribedIds).ToArray())
                _lastSnapshots.TryRemove(staleId, out _);

            foreach (var workloadId in subscribedIds)
            {
                try
                {
                    var logs = await guardian.ListLogsAsync(workloadId, stoppingToken);
                    var fingerprint = CreateFingerprint(logs);
                    if (_lastSnapshots.TryGetValue(workloadId, out var prior) && prior == fingerprint) continue;
                    _lastSnapshots[workloadId] = fingerprint;
                    await hub.Clients.Group(GuardianLogsHub.GroupName(workloadId)).OnLogSnapshot(logs);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch
                {
                    // The Agent can be restarted independently; a later polling pass reconnects viewers.
                }
            }
        }
    }

    private static string CreateFingerprint(IReadOnlyList<GuardianLogEntryDto> logs) => string.Join('\u001e',
        logs.Select(log => $"{log.Timestamp.UtcTicks}\u001f{log.Stream}\u001f{log.Message}"));
}
