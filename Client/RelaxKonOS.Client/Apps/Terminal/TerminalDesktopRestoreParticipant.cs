using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.DesktopRestore;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Restores already-running server PTYs when an authenticated desktop becomes ready.</summary>
public sealed class TerminalDesktopRestoreParticipant(DeveloperPackageManager packages,
    ApplicationManager applications, DesktopDevicePreferences preferences) : IDesktopRestoreParticipant
{
    private static readonly AppId Terminal = new("relaxkonos.terminal");

    public int Order => 100;

    public Task RestoreAsync(DesktopRestoreContext context, CancellationToken cancellationToken)
    {
        if (context.Session.State != AuthSessionState.Authenticated || !preferences.Value.RestoreTerminals
            || applications.GetManifest(Terminal) is not { } manifest || !applications.EvaluateCompatibility(manifest).IsCompatible)
            return Task.CompletedTask;

        return packages.RestoreTerminalSessionsAsync(cancellationToken);
    }
}
