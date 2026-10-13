using Avalonia.Controls;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// One rendering surface for one terminal session.
///
/// Both implementations — the in-process terminal control and the xterm.js page in a WebView — drive
/// the same <see cref="ITerminalTransport"/> instances, so which engine a window uses changes how a
/// session is drawn, never how it is reached. Selecting an engine therefore stays a presentation
/// decision: a server PTY over SignalR, an SSH channel and a local PTY all work in either one.
/// </summary>
public interface ITerminalSurface : IDisposable
{
    /// <summary>The control to place in the session host.</summary>
    Control View { get; }

    /// <summary>True while the surface holds a selection the user can copy.</summary>
    bool HasSelection { get; }

    /// <summary>Raised with the child's exit code (or a transport failure) when the session ends.</summary>
    event EventHandler<int>? ProcessExited;

    /// <summary>Raised when the session reports a window/OSC title.</summary>
    event EventHandler<string>? TitleChanged;

    /// <summary>Raised when the copyable selection changes.</summary>
    event EventHandler? SelectionChanged;

    Task StartSessionAsync(ITerminalTransportOptions options, CancellationToken cancellationToken);

    /// <summary>Releases this surface's transport. Killing the server session is the caller's job.</summary>
    void StopSession();

    void Focus();

    Task CopySelectionAsync();

    Task PasteAsync();

    void SelectAll();

    /// <summary>Drops the visible rows and the scrollback.</summary>
    void ClearScreen();

    /// <summary>Applies the workspace's font and palette to the engine that is actually rendering.</summary>
    void ApplyAppearance(TerminalSettingsDto appearance);

    /// <summary>Applies the current display language to any in-page chrome.</summary>
    void ApplyLabels(IReadOnlyDictionary<string, string> labels);

    /// <summary>
    /// Hides or restores a native child surface. A WebView is an operating-system window that does not
    /// take part in Avalonia's composition, so it has to be withdrawn while its window is inactive,
    /// dragging or showing a dialog.
    /// </summary>
    void SetNativeVisible(bool visible);
}
