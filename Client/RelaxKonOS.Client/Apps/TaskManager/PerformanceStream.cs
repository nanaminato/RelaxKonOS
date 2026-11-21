using RelaxKonOS.Client.Services.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>任务管理器性能页的单窗口实时订阅。重连后由 ViewModel 回补 REST history，事件按 Sequence 去重。</summary>
internal sealed class PerformanceStream(IAuthSession session, Func<AppCapabilityResult> readiness) : IServerPerformanceStream
{
    private HubConnection? _connection;

    public event Action<PerformanceRealtimeSnapshotDto>? SnapshotReceived;
    public event Action? Reconnected;
    public event Action? Disconnected;

    public async Task<AppCapabilityResult> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = readiness();
        if (status != AppCapabilityResult.Succeeded) return status;
        if (session.EffectiveBaseUrl is not { } baseUrl || session.Tokens is null)
            return AppCapabilityResult.Unavailable;
        if (_connection is not null) return AppCapabilityResult.Succeeded;

        var hubUrl = new Uri(new Uri(baseUrl), RelaxKonOSEndpoints.PerformanceHubPath.TrimStart('/'));
        bool IsCurrent() => readiness() == AppCapabilityResult.Succeeded && session.EffectiveBaseUrl == baseUrl;
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                ServerCertificateTrust.ConfigureSignalR(options, hubUrl);
                options.AccessTokenProvider = async () =>
                {
                    if (!IsCurrent()) return null;
                    var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1));
                    return IsCurrent() ? token : null;
                };
            })
            .WithAutomaticReconnect()
            .Build();
        connection.On<PerformanceRealtimeSnapshotDto>(PerformanceHubEvents.OnPerformanceSnapshot, snapshot =>
        {
            if (IsCurrent() && ReferenceEquals(_connection, connection)) SnapshotReceived?.Invoke(snapshot);
        });
        connection.Reconnected += async _ =>
        {
            if (!IsCurrent()) return;
            await SubscribeAsync(connection, CancellationToken.None);
            if (IsCurrent()) Reconnected?.Invoke();
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
            if (IsCurrent()) await SubscribeAsync(connection, cancellationToken);
            if (IsCurrent()) return AppCapabilityResult.Succeeded;
            await DisposeAsync();
            return readiness() == AppCapabilityResult.PermissionDenied
                ? AppCapabilityResult.PermissionDenied : AppCapabilityResult.Unavailable;
        }
        catch
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _connection, null, connection), connection))
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
