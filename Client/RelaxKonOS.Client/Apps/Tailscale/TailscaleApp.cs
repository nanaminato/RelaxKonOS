using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Apps.Tailscale.Views;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Tailscale;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Tailscale;

public sealed class TailscaleApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.tailscale"),
        "Tailscale", "0.1.0", "◉", "Tailscale", [],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Tailscale]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var language = context.Services.GetRequiredService<LocalizationService>();
        var model = new TailscaleViewModel(context.Services.GetRequiredService<ITailscaleClient>(),
            context.Services.GetRequiredService<ITailscaleOperationReferences>(), context.Services.GetRequiredService<IHostElevationBroker>(), language);
        var window = context.ShowWindow(LocalizedText.Get("tailscale.title"), new TailscaleWorkspace(model),
            new Rect(100, 80, 980, 640), Manifest.IconGlyph);
        model.EditConfigurationAsync = async configuration =>
        {
            TailscaleConfiguration? result = null;
            await context.ShowDialogAsync<TailscaleConfiguration?>(window, LocalizedText.Get("tailscale.configure"), dialog =>
                new TailscaleConfigurationDialog(configuration, value => { result = value; dialog.Close(value); }), new Size(520, 340));
            return result;
        };
        model.ConfirmAsync = async (operation, baseline, target) =>
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("tailscale.preview"), dialog =>
                new TailscaleConfirmationDialog(operation, baseline, target, language, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        model.ConfirmCloseAsync = async (preview, target) =>
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("tailscale.recovery.close"), dialog =>
                new TailscaleRecoveryDialog(preview, target, language, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };

    }
}
