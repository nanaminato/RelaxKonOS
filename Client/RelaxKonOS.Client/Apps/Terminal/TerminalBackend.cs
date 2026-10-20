using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Which terminal engine renders a session in the desktop client.</summary>
public enum TerminalBackendKind
{
    /// <summary>The in-process RoyalTerminal control.</summary>
    BuiltIn = 0,

    /// <summary>Legacy wire value retained for compatibility; desktop no longer instantiates xterm.js.</summary>
    XtermJs = 1,
}

/// <summary>
/// The backend a window actually uses. The fallback field is retained so older workspace settings
/// can be consumed without changing the view-model contract.
/// </summary>
public readonly record struct TerminalBackendSelection(TerminalBackendKind Backend, string? FallbackReasonKey)
{
    public bool HasFallback => FallbackReasonKey is not null;
}

/// <summary>
/// Desktop terminals are rendered exclusively by RoyalTerminal. Older workspaces may still contain
/// the historical xterm.js backend token; it is intentionally migrated to the built-in backend
/// without surfacing an error to the user.
/// </summary>
public static class TerminalBackendSelector
{
    public static bool WebViewHostAvailable => false;

    public static TerminalBackendKind FromWire(string? backend) => TerminalBackendKind.BuiltIn;

    public static string ToWire(TerminalBackendKind backend) => TerminalBackends.BuiltIn;

    public static bool IsAvailable(TerminalBackendKind backend, bool webViewHostAvailable) =>
        backend == TerminalBackendKind.BuiltIn;

    public static TerminalBackendSelection Resolve(string? requestedBackend, bool webViewHostAvailable) =>
        new(TerminalBackendKind.BuiltIn, null);

    public static TerminalBackendSelection Resolve(string? requestedBackend) =>
        new(TerminalBackendKind.BuiltIn, null);
}
