using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Server.ProcessGuardian;

namespace RelaxKonOS.Server.Hubs;

/// <summary>Authenticated subscriptions for sanitized Guardian workload logs.</summary>
[Authorize]
public sealed class GuardianLogsHub(
    IProcessGuardianService guardian,
    GuardianLogSubscriptionRegistry subscriptions) : Hub<IGuardianLogsHubClient>
{
    public async Task<IReadOnlyList<GuardianLogEntryDto>> Subscribe(string workloadId)
    {
        if (string.IsNullOrWhiteSpace(workloadId)) throw new HubException("A workload ID is required.");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(workloadId), Context.ConnectionAborted);
        subscriptions.Subscribe(Context.ConnectionId, workloadId);
        return await guardian.ListLogsAsync(workloadId, Context.ConnectionAborted);
    }

    public async Task Unsubscribe(string workloadId)
    {
        if (string.IsNullOrWhiteSpace(workloadId)) return;
        subscriptions.Unsubscribe(Context.ConnectionId, workloadId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(workloadId), Context.ConnectionAborted);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    internal static string GroupName(string workloadId) => $"guardian-logs:{workloadId}";
}
