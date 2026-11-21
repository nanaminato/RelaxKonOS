using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.App.Terminal;

public sealed class TerminalPackageApp : IExternalRemoteApplication, IExternalTerminalApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.terminal"), "Terminal", "1.0.0", "🖥", "Remote terminal",
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Terminal]));

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default) =>
        TerminalWorkspace.ActivateAsync(context, false,
            (id, administrator) => Open(context, id, null, administrator), cancellationToken);

    public Task RestoreAsync(IExternalAppContext context, CancellationToken cancellationToken) =>
        TerminalWorkspace.ActivateAsync(context, true,
            (id, administrator) => Open(context, id, null, administrator), cancellationToken);

    public void OpenAt(IExternalAppContext context, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Open(context, null, workingDirectory, false);
    }

    private void Open(IExternalAppContext context, string? sessionId, string? directory, bool administrator)
    {
        var terminal = context.Terminal ?? throw new InvalidOperationException("The terminal capability is unavailable.");
        TerminalWorkspace.Open(context, Manifest.IconGlyph,
            (restoredId, workingDirectory, elevated) => new TerminalViewModel(context.SystemLanguage,
                terminal, restoredId, workingDirectory, elevated, context.TerminalWorkspacePreferences, context.TerminalDevicePreferences),
            context.Settings.OpenEnvironmentAsync,
            workingDirectory => Open(context, null, workingDirectory, true),
            sessionId, directory, administrator);
    }
}
