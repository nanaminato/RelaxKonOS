using Avalonia.Controls;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// One rendering surface for one terminal session. Desktop currently uses the in-process
/// RoyalTerminal control; the abstraction remains useful for keeping session lifetime and UI hosting
/// separate and leaves room for platform-specific frontends in other clients.
/// </summary>
public interface ITerminalSurface : IDisposable
{
    Control View { get; }

    bool HasSelection { get; }

    event EventHandler<int>? ProcessExited;

    event EventHandler<string>? TitleChanged;

    event EventHandler? SelectionChanged;

    Task StartSessionAsync(ITerminalTransportOptions options, CancellationToken cancellationToken);

    void StopSession();

    void Focus();

    Task CopySelectionAsync();

    Task PasteAsync();

    void SelectAll();

    void ClearScreen();

    void ApplyAppearance(TerminalSettingsDto appearance);

    void ApplyLabels(IReadOnlyDictionary<string, string> labels);

    /// <summary>Platform hook retained by the host; the Avalonia RoyalTerminal surface is a no-op.</summary>
    void SetNativeVisible(bool visible);
}

/// <summary>
/// Retained for compatibility with the view-model's defensive surface-start fallback path. The
/// RoyalTerminal desktop surface does not normally throw this exception.
/// </summary>
public sealed class TerminalSurfaceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
