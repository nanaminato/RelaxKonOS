using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Which terminal engine renders a session in the desktop client.</summary>
public enum TerminalBackendKind
{
    /// <summary>The in-process terminal control. Available on every platform this client ships for.</summary>
    BuiltIn = 0,

    /// <summary>An xterm.js page hosted in the platform WebView. Needs an embedded WebView host.</summary>
    XtermJs = 1,
}

/// <summary>
/// The backend a window actually uses, together with the reason it differs from what the workspace
/// asked for. A null <see cref="FallbackReasonKey"/> means the request was honoured.
/// </summary>
public readonly record struct TerminalBackendSelection(TerminalBackendKind Backend, string? FallbackReasonKey)
{
    public bool HasFallback => FallbackReasonKey is not null;
}

/// <summary>
/// Chooses the terminal engine for one session.
///
/// The workspace setting is a request, not a command: it travels between devices, while the ability
/// to host a WebView is a property of the machine in front of the user. The desktop therefore
/// resolves the request against the local platform and reports a localized reason when it has to
/// ignore it, instead of opening a terminal that cannot render.
/// </summary>
public static class TerminalBackendSelector
{
    /// <summary>True when this process can host an embedded WebView.</summary>
    /// <remarks>
    /// Linux deliberately delegates web pages to the system browser process (see the built-in
    /// browser), so there is no embedded WebView host to put xterm.js in.
    /// </remarks>
    public static bool WebViewHostAvailable => !OperatingSystem.IsLinux();

    public static TerminalBackendKind FromWire(string? backend) =>
        string.Equals(backend?.Trim(), TerminalBackends.XtermJs, StringComparison.OrdinalIgnoreCase)
            ? TerminalBackendKind.XtermJs
            : TerminalBackendKind.BuiltIn;

    public static string ToWire(TerminalBackendKind backend) =>
        backend == TerminalBackendKind.XtermJs ? TerminalBackends.XtermJs : TerminalBackends.BuiltIn;

    /// <summary>True when <paramref name="backend"/> can actually run on this host.</summary>
    public static bool IsAvailable(TerminalBackendKind backend, bool webViewHostAvailable) =>
        backend != TerminalBackendKind.XtermJs || webViewHostAvailable;

    /// <summary>Resolves the stored request into the engine this window should use.</summary>
    public static TerminalBackendSelection Resolve(string? requestedBackend, bool webViewHostAvailable)
    {
        if (!TerminalBackends.TryNormalize(requestedBackend, out var token))
            return new(TerminalBackendKind.BuiltIn, "terminal.backend.fallback.unknown");

        var backend = FromWire(token);
        return IsAvailable(backend, webViewHostAvailable)
            ? new TerminalBackendSelection(backend, null)
            : new TerminalBackendSelection(TerminalBackendKind.BuiltIn, "terminal.backend.fallback.webview_unavailable");
    }

    /// <summary>Resolves against the current platform.</summary>
    public static TerminalBackendSelection Resolve(string? requestedBackend) =>
        Resolve(requestedBackend, WebViewHostAvailable);
}
