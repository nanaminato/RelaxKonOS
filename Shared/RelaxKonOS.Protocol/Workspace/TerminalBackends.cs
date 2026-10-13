namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// Canonical terminal engine identifiers for a workspace's terminal settings.
///
/// The value is a short token rather than an enum member name because it is persisted verbatim in
/// the workspace registry document, where an administrator reads and edits it next to the other
/// terminal fields. A backend names <em>which terminal engine renders the session</em>; it never
/// changes what the PTY or the shell process does, and every backend speaks to the same
/// <c>ITerminalTransport</c> implementations (server PTY over SignalR, SSH, local PTY).
/// </summary>
public static class TerminalBackends
{
    /// <summary>The in-process terminal control shipped with the desktop client. Available everywhere.</summary>
    public const string BuiltIn = "builtin";

    /// <summary>An xterm.js emulator hosted in the platform WebView. Requires an embedded WebView host.</summary>
    public const string XtermJs = "xterm";

    /// <summary>Every backend this build understands, in presentation order.</summary>
    public static IReadOnlyList<string> All { get; } = [BuiltIn, XtermJs];

    /// <summary>
    /// Maps a stored value onto a shipped backend. A missing value (a document written before the
    /// field existed) is the built-in terminal, and an unknown token is rejected rather than
    /// silently downgraded — the caller decides whether that is a bad request or a fallback.
    /// </summary>
    public static bool TryNormalize(string? value, out string backend)
    {
        backend = BuiltIn;
        if (value is null)
            return true;

        var candidate = value.Trim();
        if (candidate.Length == 0)
            return true;

        foreach (var known in All)
        {
            if (!string.Equals(known, candidate, StringComparison.OrdinalIgnoreCase))
                continue;
            backend = known;
            return true;
        }

        return false;
    }
}
