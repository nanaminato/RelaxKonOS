using RoyalTerminal.Terminal;
using RelaxKonOS.Client.Services.Diagnostics;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// SignalR 远端终端传输选项。<see cref="HubUrl"/> 指向服务端 Terminal Hub（<c>/hubs/terminals</c>），
/// 鉴权用 <see cref="TokenProvider"/>（每次连接/重连取最新 JWT）或静态 <see cref="AccessToken"/>。
/// <see cref="SessionId"/> 非空时附加到既有持久会话（恢复），为 null 时新建会话。
/// </summary>
public sealed class SignalRTransportOptions : ITerminalTransportOptions
{
    public string TransportId => "signalr";
    public TerminalSessionDimensions Dimensions { get; }

    public string HubUrl { get; }
    public string? AccessToken { get; }
    public Func<Task<string?>>? TokenProvider { get; }
    public string? Shell { get; }
    public string? WorkingDirectory { get; }
    public string? SessionId { get; }
    public NetworkDiagnosticsService? Diagnostics { get; }

    public SignalRTransportOptions(
        string hubUrl,
        TerminalSessionDimensions dimensions,
        Func<Task<string?>>? tokenProvider = null,
        string? accessToken = null,
        string? shell = null,
        string? workingDirectory = null,
        string? sessionId = null,
        NetworkDiagnosticsService? diagnostics = null)
    {
        HubUrl = hubUrl;
        Dimensions = dimensions;
        TokenProvider = tokenProvider;
        AccessToken = accessToken;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        SessionId = sessionId;
        Diagnostics = diagnostics;
    }
}
