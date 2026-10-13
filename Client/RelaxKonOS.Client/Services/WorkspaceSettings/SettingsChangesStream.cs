using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

/// <summary>A connection belongs to one immutable authenticated target; it never takes a token from another login.</summary>
public static class SettingsChangesStream
{
    public static async Task RunAsync(string url, string token, Guid workspaceId,
        Func<Task> refresh, Func<RegistrySettingsChanged, Task> invalidated, Func<Task> renew, CancellationToken cancellationToken)
    {
        var retrySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(url), RelaxKonOSEndpoints.SettingsChangesHubPath.TrimStart('/')),
                    options =>
                    {
                        ServerCertificateTrust.ConfigureSignalR(options, new Uri(url));
                        options.AccessTokenProvider = async () =>
                        {
                            await renew();
                            cancellationToken.ThrowIfCancellationRequested();
                            return token;
                        };
                    })
                .WithAutomaticReconnect().Build();
            connection.On<WorkspaceSettingsChanged>(SettingsChangesMethods.Changed, async change =>
            {
                if (change.WorkspaceId == workspaceId && !cancellationToken.IsCancellationRequested) await refresh();
            });
            connection.On<RegistrySettingsChanged>(SettingsChangesMethods.RegistryChanged, async change =>
            {
                if (!cancellationToken.IsCancellationRequested) await invalidated(change);
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
                await renew();
                cancellationToken.ThrowIfCancellationRequested();
                await connection.StartAsync(cancellationToken);
                await connection.InvokeAsync(SettingsChangesMethods.Subscribe, workspaceId, cancellationToken);
                // Subscribe first, then read: reconnect cannot leave a missed-change gap.
                await refresh();
                retrySeconds = 1;
                await closed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { /* A read-only subscription is safe to retry. Writes are never queued here. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken); retrySeconds = Math.Min(60, retrySeconds * 2); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
