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
        ISshHostKeyValidator hostKeys,
        Func<IReadOnlyDictionary<string, string>>? labels = null)
    {
        _credentials = credentials;
        _hostKeys = hostKeys;
        Labels = labels ?? TerminalSurfaceLabels.Build;
        TransportFactory = new SignalRTransportFactory(credentials, hostKeys);
    }

    public SignalRTransportFactory TransportFactory { get; }

    public Func<IReadOnlyDictionary<string, string>> Labels { get; }

    public ITerminalSurface Create(TerminalBackendKind backend) =>
        new BuiltInTerminalSurface(TransportFactory, _credentials, _hostKeys);
}

public static class TerminalSurfaceLabels
{
    public static IReadOnlyDictionary<string, string> Build() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["copy"] = Localization.LocalizedText.Get("terminal.context.copy"),
        ["paste"] = Localization.LocalizedText.Get("terminal.context.paste"),
        ["selectAll"] = Localization.LocalizedText.Get("terminal.context.select_all"),
        ["clear"] = Localization.LocalizedText.Get("terminal.context.clear"),
    };
}
