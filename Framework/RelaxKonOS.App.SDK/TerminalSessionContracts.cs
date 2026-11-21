using Avalonia.Controls;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.AppSDK;

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

/// <summary>Creates a terminal session using the host's current connection and credentials.</summary>
public interface IExternalTerminal
{
    bool CanOpenAdministratorTerminal { get; }
    IExternalTerminalSession CreateSession(string vtEngine, string? sessionId, string? workingDirectory, bool isAdministrator);
}

/// <summary>A terminal view and the lifecycle of its transport.</summary>
public interface IExternalTerminalSession : IDisposable
{
    ITerminalSurface Surface { get; }
    bool IsRemote { get; }
    bool HasAuthenticatedServer { get; }
    string? SessionId { get; }
    Task StartAsync(CancellationToken cancellationToken);
}

public interface IExternalTerminalApplication
{
    Task RestoreAsync(IExternalAppContext context, CancellationToken cancellationToken);
    void OpenAt(IExternalAppContext context, string workingDirectory);
}
