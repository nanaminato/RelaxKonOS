using RelaxKonOS.Client.Services.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>任务管理器性能页的单窗口实时订阅。重连后由 ViewModel 回补 REST history，事件按 Sequence 去重。</summary>
public sealed class PerformanceStream(IAuthSession session) : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<PerformanceRealtimeSnapshotDto>? SnapshotReceived;
    public event Action? Reconnected;
    public event Action? Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Not signed in.");
        if (_connection is not null) return;

        var hubUrl = new Uri(new Uri(session.ServerUrl), RelaxKonOSEndpoints.PerformanceHubPath.TrimStart('/')).ToString();
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => session.GetAccessTokenAsync(TimeSpan.FromMinutes(1)))
            .WithAutomaticReconnect()
            .Build();
        connection.On<PerformanceRealtimeSnapshotDto>(PerformanceHubEvents.OnPerformanceSnapshot, snapshot => SnapshotReceived?.Invoke(snapshot));
        connection.Reconnected += async _ =>
        {
            await SubscribeAsync(connection, CancellationToken.None);
            Reconnected?.Invoke();
        };
        connection.Closed += _ =>
        {
            Disconnected?.Invoke();
            return Task.CompletedTask;
        };

        _connection = connection;
        try
        {
            await connection.StartAsync(cancellationToken);
            await SubscribeAsync(connection, cancellationToken);
        }
        catch
        {
            if (ReferenceEquals(_connection, connection)) _connection = null;
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;
        try
        {
            if (connection.State == HubConnectionState.Connected)
                await connection.InvokeAsync(PerformanceHubMethods.Unsubscribe);
        }
        catch { /* Closing a window never depends on a working network. */ }
        await connection.DisposeAsync();
    }

    private static Task SubscribeAsync(HubConnection connection, CancellationToken cancellationToken)
        => connection.InvokeAsync(PerformanceHubMethods.Subscribe, cancellationToken);
}
