using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Protocol.Hubs;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalTerminalSessionDiscovery(ExternalServerConnection connection,
    NetworkDiagnosticsService? diagnostics) : ITerminalSessionDiscovery
{
    public bool IsCurrent => connection.IsCurrent;

    public async Task<IReadOnlyList<TerminalSessionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrent || connection.Session.EffectiveBaseUrl is not { } url)
            throw new InvalidOperationException("The terminal discovery connection is no longer current.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var invalidation = connection.ObserveInvalidation(lifetime.Cancel);
        var options = new SignalRTransportOptions(url.TrimEnd('/') + "/hubs/terminals",
            new TerminalSessionDimensions(80, 24, 800, 480),
            tokenProvider: () => IsCurrent ? connection.Session.GetAccessTokenAsync(TimeSpan.FromMinutes(1)) : Task.FromResult<string?>(null),
            diagnostics: diagnostics);
        var sessions = await TerminalHubConnection.ListSessionsAsync(options, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        if (!IsCurrent) throw new InvalidOperationException("The terminal discovery connection is no longer current.");
        return sessions;
    }
}
