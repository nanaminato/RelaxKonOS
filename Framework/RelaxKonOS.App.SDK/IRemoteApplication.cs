using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.AppSDK;

/// <summary>
/// A RelaxKonOS application. Implementations produce UI and call <see cref="Activate"/> to open
/// their initial window(s) via the supplied <see cref="AppContext"/>.
/// </summary>
public interface IRemoteApplication
{
    /// <summary>Static metadata used by desktop, start menu and taskbar.</summary>
    ApplicationManifest Manifest { get; }

    /// <summary>Called by the runtime when the user launches the application.</summary>
    void Activate(AppContext context);
}
