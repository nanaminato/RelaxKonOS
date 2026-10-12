using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Builds the terminal surface a window asks for.
///
/// One factory belongs to one tab: it owns the transport factory, so the session a tab is attached to
/// stays addressable (current session id, stop, kill) no matter which engine is currently drawing it.
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

    /// <summary>The transport factory every surface of this tab shares.</summary>
    public SignalRTransportFactory TransportFactory { get; }

    /// <summary>Current display-language labels for chrome drawn inside a web-rendered surface.</summary>
    public Func<IReadOnlyDictionary<string, string>> Labels { get; }

    public ITerminalSurface Create(TerminalBackendKind backend) => backend == TerminalBackendKind.XtermJs
        ? new XtermJsTerminalSurface(TransportFactory, Labels())
        : new BuiltInTerminalSurface(TransportFactory, _credentials, _hostKeys);
}

/// <summary>
/// The clipboard and reset actions offered inside a web-rendered terminal. They are resolved through
/// the shared resource table so the in-page menu follows the display language like the rest of the UI.
/// </summary>
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
