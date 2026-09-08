using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RoyalTerminal.Terminal;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Built-in terminal application. A restored server session always receives its own desktop
/// window; the terminal never multiplexes multiple sessions inside a single window.
/// </summary>
public sealed class TerminalApp : RemoteApplicationBase, IOpenTerminalApplication
{
    // Desktop restoration and an early manual launch may overlap. Serialize them instead of
    // dropping the manual request while the restore-only list request is in flight.
    private static readonly SemaphoreSlim Opening = new(1, 1);

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.terminal"),
        DisplayName: "Terminal",
        Version: "1.0.0",
        IconGlyph: "🖥",
        Description: "远程终端");

    public override void Activate(AppContext context)
    {
        _ = OpenAsync(context, restoreOnly: false, cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Restores every live server PTY that is not already represented by a terminal window.
    /// Unlike ordinary user activation, this never creates a fresh terminal when no PTY exists
    /// or when the server cannot be reached.
    /// </summary>
    public async Task RestoreExistingSessionsAsync(AppContext context, CancellationToken cancellationToken)
    {
        await OpenAsync(context, restoreOnly: true, cancellationToken: cancellationToken);
    }

    /// <summary>Opens a fresh terminal at a caller-supplied remote-host directory.</summary>
    public void OpenTerminal(AppContext context, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var session = context.Services.GetService<IAuthSession>();
        var diagnostics = context.Services.GetService<NetworkDiagnosticsService>();
        OpenWindow(context, session, diagnostics, sessionId: null, workingDirectory: workingDirectory);
    }

    private async Task OpenAsync(AppContext context, bool restoreOnly, CancellationToken cancellationToken)
    {
        await Opening.WaitAsync(cancellationToken);
        try
        {
            var session = context.Services.GetService<IAuthSession>();
            var diagnostics = context.Services.GetService<NetworkDiagnosticsService>();
            var sessionIds = Array.Empty<string>();

            if (session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
            {
                try
                {
                    var options = new SignalRTransportOptions(
                        url.TrimEnd('/') + "/hubs/terminals",
                        new TerminalSessionDimensions(80, 24, 800, 480),
                        tokenProvider: () => session.GetAccessTokenAsync(TimeSpan.FromMinutes(1)),
                        accessToken: tokens.AccessToken,
                        diagnostics: diagnostics);
                    sessionIds = (await TerminalHubConnection.ListSessionsAsync(options, cancellationToken))
                        .Where(x => !x.HasExited && !TerminalViewModel.IsSessionOpen(x.SessionId))
                        .OrderBy(x => x.CreatedAt)
                        .Select(x => x.SessionId)
                        .ToArray();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Manual activation falls back to a new terminal. Desktop restoration is
                    // intentionally restore-only and leaves the desktop unchanged on failure.
                    if (restoreOnly)
                        return;
                }
            }

            // The desktop might have been closed while the one-shot list request was in flight.
            // Never turn a restore into a local fallback or a late window in that case.
            if (restoreOnly && (cancellationToken.IsCancellationRequested
                || session?.State != AuthSessionState.Authenticated))
                return;

            // No restorable process means this activation starts one fresh terminal. If every
            // existing process is already represented by a window, this is also the explicit
            // way to open an additional terminal from the desktop.
            if (sessionIds.Length == 0)
            {
                if (!restoreOnly)
                    OpenWindow(context, session, diagnostics, null);
            }
            else
                foreach (var sessionId in sessionIds)
                {
                    if (restoreOnly && cancellationToken.IsCancellationRequested)
                        return;

                    if (TerminalViewModel.TryReserveSession(sessionId))
                        OpenWindow(context, session, diagnostics, sessionId);
                }
        }
        finally
        {
            Opening.Release();
        }
    }

    private void OpenWindow(
        AppContext context,
        IAuthSession? session,
        NetworkDiagnosticsService? diagnostics,
        string? sessionId,
        string? workingDirectory = null)
    {
        var settingsClient = context.Services.GetRequiredService<ITerminalSettingsClient>();
        var viewModel = new TerminalViewModel(session, settingsClient, diagnostics, sessionId, workingDirectory);
        var view = new TerminalView
        {
            DataContext = viewModel,
        };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.terminal.display_name"), view,
            bounds: new Rect(120, 80, 820, 540),
            iconGlyph: Manifest.IconGlyph);
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("terminal.settings"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new TerminalSettingsView { DataContext = viewModel };
            }, new Size(460, 330));
        };
    }
}
