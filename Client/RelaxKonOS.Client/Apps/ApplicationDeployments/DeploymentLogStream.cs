using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>Retries initial connections as well as disconnects; each subscribe restores the tail.</summary>
internal sealed class DeploymentLogStream : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Task running;
    private readonly Guid operationId;
    private readonly Action<DeploymentLiveLogSnapshot> receive;
    private HubConnection? connection;

    public DeploymentLogStream(IAuthSession session, Guid operationId, Action<DeploymentLiveLogSnapshot> receive, Action<bool> connectionChanged)
    {
        this.operationId = operationId;
        this.receive = receive;
        running = RunAsync(session, connectionChanged);
    }

    private async Task RunAsync(IAuthSession session, Action<bool> connectionChanged)
    {
        var token = stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                connectionChanged(false);
                if (session.EffectiveBaseUrl is null) return;
                await using var hub = new HubConnectionBuilder()
                    .WithUrl(new Uri(new Uri(session.EffectiveBaseUrl), RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath.TrimStart('/')),
                        options => options.AccessTokenProvider = () => session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: token))
                    .Build();
                connection = hub;
                using var handler = hub.On<DeploymentLiveLogSnapshot>(nameof(IApplicationDeploymentLogsClient.OnDeploymentLogs), receive);
                try
                {
                    await hub.StartAsync(token);
                    receive(await hub.InvokeAsync<DeploymentLiveLogSnapshot>(ApplicationDeploymentLogsHubMethods.Subscribe, operationId, token));
                    connectionChanged(true);
                    while (hub.State == HubConnectionState.Connected) await Task.Delay(500, token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { connectionChanged(false); }
                connection = null;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { connection = null; }
    }

    public async ValueTask DisposeAsync()
    {
        // Fetch once more after REST reports completion so the last half-second of output is kept.
        if (connection is { State: HubConnectionState.Connected } hub)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { receive(await hub.InvokeAsync<DeploymentLiveLogSnapshot>(ApplicationDeploymentLogsHubMethods.Subscribe, operationId, timeout.Token)); }
            catch { /* Disposal must work offline too. */ }
        }
        await stopping.CancelAsync();
        await running;
        stopping.Dispose();
    }
}
