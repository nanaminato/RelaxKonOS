using RelaxKonOS.App.Tailscale;
using RelaxKonOS.App.Tailscale.Views;
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
        "Tailscale", "0.1.0", "◉", "Tailscale", [AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Tailscale]),
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/tailscale.png",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var language = context.Services.GetRequiredService<LocalizationService>();
        var text = new TailscaleText(language, (key, fallback) => TailscaleLocalization.Resolve(language, key, fallback));
        var model = new TailscaleViewModel(context.Services.GetRequiredService<IServerTailscale>(),
            context.Services.GetRequiredService<ITailscaleOperationReferences>(), context.Services.GetRequiredService<IHostElevationBroker>(), language, text.Get);
        var login = new TailscaleLoginViewModel(context.Services.GetRequiredService<IServerTailscaleLogin>(),
            TailscaleOperationReferences.CreateLoginStore(),
            context.Services.GetRequiredService<IHostElevationBroker>(), language, text.Get)
            { HostMayHaveChanged = model.InvalidateControl, ReadBackendStateAsync = model.ReadBackendStateAsync,
              AccountLineProvider = () => model.AccountLine };
        var workspace = new TailscaleWorkspace(model, login);
        var window = context.ShowWindow(text.Get("tailscale.title", "tailscale.title"), workspace,
            new Rect(100, 80, 980, 640), Manifest.IconGlyph);
        workspace.AttachWindowHeader(window);
        login.ConfirmAsync = async (target, id, close) =>
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, text.Get("tailscale.login.title", "tailscale.login.title"), dialog =>
                new TailscaleLoginDialog(target, id, close, login.ObservedBackendState, text, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        login.OpenBrowserAsync = uri =>
        {
            // Bypass configurable browser routing: login challenges belong on this client, never on the remote host.
            try
            {
                if (!TailscaleLoginUrl.IsOfficial(uri.OriginalString)) return Task.FromResult(false);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return Task.FromResult(false); }
        };
        model.ConfirmAsync = async (operation, baseline, target) =>
        {
            var accepted = false;
            var title = operation.Action == TailscaleAction.Logout
                ? text.Get("tailscale.menu.logout", "tailscale.menu.logout") : text.Get("tailscale.preview", "tailscale.preview");
            await context.ShowDialogAsync<bool>(window, title, dialog =>
                new TailscaleConfirmationDialog(operation, baseline, target, text, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        model.ConfirmCloseAsync = async (preview, target) =>
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, text.Get("tailscale.recovery.close", "tailscale.recovery.close"), dialog =>
                new TailscaleRecoveryDialog(preview, target, text, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        model.EditPreferencesDialogAsync = async (preferences, exitNodes, platform, peersRestricted) =>
        {
            TailscalePreferences? result = null;
            await context.ShowDialogAsync<TailscalePreferences?>(window, text.Get("tailscale.preferences.title", "tailscale.preferences.title"), dialog =>
                new TailscalePreferencesDialog(preferences, exitNodes, platform, peersRestricted, text, value => { result = value; dialog.Close(value); }), new Size(560, 600));
            return result;
        };
        model.OpenAdminConsoleHandler = () =>
        {
            // A fixed official page opened on this client; the host is never asked to browse.
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://login.tailscale.com/admin/machines") { UseShellExecute = true });
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return Task.CompletedTask; }
        };
        model.ShowAboutHandler = async () =>
        {
            await context.ShowDialogAsync<bool>(window, text.Get("tailscale.about.title", "tailscale.about.title"), dialog =>
                new TailscaleAboutDialog(new TailscaleAboutText(text, Manifest.Version, model.Target, model.HostVersion,
                    model.DeviceName, model.TailnetDisplay, model.AccountLogin), () => dialog.Close(true)), new Size(520, 360));
        };

    }
}
