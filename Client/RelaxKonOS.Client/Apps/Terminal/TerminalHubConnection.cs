using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Protocol.Hubs;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// 构建终端 Hub 的 <see cref="HubConnection"/>（未启动）。传输层与"拉取会话列表"的临时查询连接共用，
/// 保证鉴权（AccessTokenProvider）与 URL 拼装一致。
/// </summary>
public static class TerminalHubConnection
{
    public static HubConnection Build(SignalRTransportOptions opts)
    {
        // 不启用 WithAutomaticReconnect：自动重连后服务端不会自动重新附加会话，会进入半附加状态。
        // 恢复路径是"再次登录打开终端"→重新 Start(Attach)→服务端回放缓冲快照，而非进程内自动重连。
        var connection = new HubConnectionBuilder()
            .WithUrl(opts.HubUrl, http =>
            {
                http.AccessTokenProvider = () =>
                    opts.TokenProvider?.Invoke() ?? Task.FromResult(opts.AccessToken);
                if (opts.Diagnostics is not null)
                    http.HttpMessageHandlerFactory = inner => new NetworkDiagnosticsHandler(opts.Diagnostics, "terminal-signalr")
                    {
                        InnerHandler = inner,
                    };
            })
            .Build();
        if (opts.Diagnostics is not null)
            connection.Closed += error =>
            {
                Record(opts.Diagnostics, "Connection closed", TimeSpan.Zero,
                    error is null ? NetworkDiagnosticOutcome.Succeeded : NetworkDiagnosticOutcome.TransportError,
                    error is null ? null : NetworkDiagnosticsService.ErrorKind(error));
                return Task.CompletedTask;
            };
        return connection;
    }

    /// <summary>
    /// 用一次性连接拉取当前用户的终端会话列表（附加前调用，决定恢复哪个会话或新建）。
    /// 连接用完即释放，不复用传输层连接。
    /// </summary>
    public static async Task<List<TerminalSessionInfo>> ListSessionsAsync(
        SignalRTransportOptions opts, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var conn = Build(opts);
        try
        {
            await conn.StartAsync(ct).ConfigureAwait(false);
            var result = await conn.InvokeAsync<List<TerminalSessionInfo>>(
                TerminalHubMethods.ListSessions, ct).ConfigureAwait(false) ?? new();
            started.Stop();
            if (opts.Diagnostics is not null)
                Record(opts.Diagnostics, TerminalHubMethods.ListSessions, started.Elapsed, NetworkDiagnosticOutcome.Succeeded);
            return result;
        }
        catch (Exception exception)
        {
            started.Stop();
            if (opts.Diagnostics is not null)
                Record(opts.Diagnostics, TerminalHubMethods.ListSessions, started.Elapsed,
                    exception is OperationCanceledException ? NetworkDiagnosticOutcome.Cancelled : NetworkDiagnosticOutcome.TransportError,
                    NetworkDiagnosticsService.ErrorKind(exception));
            throw;
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static void Record(NetworkDiagnosticsService diagnostics, string name, TimeSpan duration,
        NetworkDiagnosticOutcome outcome, string? errorKind = null) => diagnostics.Record(new NetworkDiagnosticEntry(
            0, DateTimeOffset.UtcNow - duration, duration, NetworkDiagnosticKind.SignalR, "terminal", name,
            null, "/hubs/terminals", outcome, null, null, null, false, errorKind));
}
