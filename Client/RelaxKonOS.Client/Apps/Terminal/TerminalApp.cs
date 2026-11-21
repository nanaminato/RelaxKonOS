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
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Built-in terminal application. A restored server session always receives its own desktop
/// window; manually created sessions can share that window as tabs.
/// </summary>
public sealed class TerminalApp : RemoteApplicationBase, IOpenTerminalApplication
{
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

    private Task OpenAsync(AppContext context, bool restoreOnly, CancellationToken cancellationToken)
    {
        var services = context.Services;
        var session = services.GetService<IAuthSession>();
        var diagnostics = services.GetService<NetworkDiagnosticsService>();
        var sdk = services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        return TerminalWorkspace.ActivateAsync(sdk, restoreOnly,
            (sessionId, administrator) => OpenWindow(context, session, diagnostics, sessionId, isAdministrator: administrator),
            cancellationToken);
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
        TerminalWorkspace.Open(sdk, Manifest.IconGlyph,
            (restoredId, directory, administrator) => new TerminalViewModel(session, diagnostics,
                services.GetService<SshDesktopSession>(), restoredId, directory, administrator,
                sdk.TerminalWorkspacePreferences, sdk.TerminalDevicePreferences),
            services.GetRequiredService<ISettingsNavigation>().OpenEnvironmentAsync,
            directory => OpenWindow(context, session, diagnostics, null, directory, isAdministrator: true),
            sessionId, workingDirectory, isAdministrator);
    }
}
