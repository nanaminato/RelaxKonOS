using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Welcome;

/// <summary>Built-in "Welcome" application — a first-run intro window.</summary>
public sealed class WelcomeApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.welcome"),
        DisplayName: "Welcome",
        Version: "1.0.0",
        IconGlyph: "🏠",
        Description: "Get started with RelaxKonOS");

    public override void Activate(AppContext context)
    {
        var view = new WelcomeView { DataContext = new WelcomeViewModel() };
        context.ShowWindow("Welcome to RelaxKonOS", view,
            bounds: new Rect(120, 80, 720, 480),
            iconGlyph: Manifest.IconGlyph);
    }
}
