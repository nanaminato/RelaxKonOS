using RelaxKonOS.App.Terminal;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.ServerCenter;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RoyalTerminal.Terminal;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Built-in terminal application. A restored server session always receives its own desktop
/// window; manually created sessions can share that window as tabs.
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
        Description: "Remote terminal",
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Terminal]));

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
            var sshDesktop = context.Services.GetService<SshDesktopSession>();
            var diagnostics = context.Services.GetService<NetworkDiagnosticsService>();
            if (sshDesktop?.IsConnected == true)
            {
                if (!restoreOnly) OpenWindow(context, session, diagnostics, null);
                return;
            }
            var sessionIds = Array.Empty<RelaxKonOS.Protocol.Hubs.TerminalSessionInfo>();

            if (session is { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens })
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
                        .Where(x => !x.HasExited && !TerminalSessionReservations.IsOpen(x.SessionId))
                        .OrderBy(x => x.CreatedAt)
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

                    if (TerminalSessionReservations.TryReserve(sessionId.SessionId))
                        OpenWindow(context, session, diagnostics, sessionId.SessionId, isAdministrator: sessionId.IsAdministrator);
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
        string? workingDirectory = null,
        bool isAdministrator = false)
    {
        var services = context.Services;
        var sdk = services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        var settingsClient = services.GetRequiredService<ITerminalSettingsClient>();
        TerminalWorkspace.Open(sdk, Manifest.IconGlyph,
            (restoredId, directory, administrator) => new TerminalViewModel(session, settingsClient, diagnostics,
                services.GetService<SshDesktopSession>(), restoredId, directory, administrator,
                services.GetRequiredService<ClientPresentationPreferences>(), services.GetRequiredService<DesktopDevicePreferences>()),
            services.GetRequiredService<ISettingsNavigation>().OpenEnvironmentAsync,
            directory => OpenWindow(context, session, diagnostics, null, directory, isAdministrator: true),
            sessionId, workingDirectory, isAdministrator);
    }
}
