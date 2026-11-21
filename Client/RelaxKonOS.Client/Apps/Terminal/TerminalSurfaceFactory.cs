using RelaxKonOS.App.Terminal;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Builds the RoyalTerminal surface for a terminal tab.
///
/// One factory belongs to one tab and owns the transport factory, so the session remains addressable
/// independently from the Avalonia control that renders it.
/// </summary>
public sealed class TerminalSurfaceFactory
{
    private readonly ISshCredentialProvider _credentials;
    private readonly ISshHostKeyValidator _hostKeys;

    public TerminalSurfaceFactory(
        ISshCredentialProvider credentials,
        ISshHostKeyValidator hostKeys)
    {
        _credentials = credentials;
        _hostKeys = hostKeys;
        TransportFactory = new SignalRTransportFactory(credentials, hostKeys);
    }

    public SignalRTransportFactory TransportFactory { get; }

    public ITerminalSurface Create(VtProcessorPreference vtEngine) =>
        new BuiltInTerminalSurface(TransportFactory, _credentials, _hostKeys, vtEngine);
}
