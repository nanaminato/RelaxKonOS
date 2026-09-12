using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

/// <summary>A connection belongs to one immutable authenticated target; it never takes a token from another login.</summary>
public static class SettingsChangesStream
{
    public static async Task RunAsync(string url, string token, Guid workspaceId,
        Func<Task> refresh, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(url), RelaxKonOSEndpoints.SettingsChangesHubPath.TrimStart('/')),
                    options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
                .WithAutomaticReconnect().Build();
            connection.On<WorkspaceSettingsChanged>(SettingsChangesMethods.Changed, async change =>
            {
                if (change.WorkspaceId == workspaceId && !cancellationToken.IsCancellationRequested) await refresh();
            });
            connection.Reconnected += async _ =>
            {
                await connection.InvokeAsync(SettingsChangesMethods.Subscribe, workspaceId, cancellationToken);
                await refresh();
            };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            try
            {
                await connection.StartAsync(cancellationToken);
                await connection.InvokeAsync(SettingsChangesMethods.Subscribe, workspaceId, cancellationToken);
                // Subscribe first, then read: reconnect cannot leave a missed-change gap.
                await refresh();
                while (!closed.Task.IsCompleted)
                {
                    // Repair a missed notification or failed REST read, and renew expiring authentication.
                    await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!closed.Task.IsCompleted) await refresh();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { /* A read-only subscription is safe to retry. Writes are never queued here. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
