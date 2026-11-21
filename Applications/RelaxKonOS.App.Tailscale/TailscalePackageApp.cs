using RelaxKonOS.App.Tailscale.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.App.Tailscale;

public sealed class TailscalePackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.tailscale"),
        "Tailscale", "0.1.0", "◉", "Tailscale", [AppPermissions.ServerComponentsRead, AppPermissions.ServerTailscaleRead, AppPermissions.ServerTailscaleManage],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Tailscale]),
        IconPath: "avares://RelaxKonOS.App.Tailscale/Assets/tailscale.png",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var language = context.SystemLanguage;
        var text = new TailscaleText(language, (key, fallback) => TailscaleLocalization.Resolve(language, key, fallback));
        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerTailscaleRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerTailscaleRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerTailscaleRead) && context.ServerConnection.IsController
            && permissions.GetStatus(AppPermissions.ServerTailscaleManage) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerTailscaleManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var model = new TailscaleViewModel(context.Tailscale, context.Tailscale.OperationReferences, language, text.Get);
        var login = new TailscaleLoginViewModel(context.TailscaleLogin, context.Tailscale.LoginReferences, language, text.Get)
            { HostMayHaveChanged = model.InvalidateControl, ReadBackendStateAsync = model.ReadBackendStateAsync,
              AccountLineProvider = () => model.AccountLine };
        var workspace = new TailscaleWorkspace(model, login);
        var window = context.Windows.ShowWindow(text.Get("tailscale.title", "tailscale.title"), workspace,
            new Rect(100, 80, 980, 640), Manifest.IconGlyph);
        workspace.AttachWindowHeader(window.Window);
        login.ConfirmAsync = async (target, id, close) =>
        {
            var accepted = false;
            await window.ShowDialogAsync<bool>(text.Get("tailscale.login.title", "tailscale.login.title"), dialog =>
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
            await window.ShowDialogAsync<bool>(title, dialog =>
                new TailscaleConfirmationDialog(operation, baseline, target, text, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        model.ConfirmCloseAsync = async (preview, target) =>
        {
            var accepted = false;
            await window.ShowDialogAsync<bool>(text.Get("tailscale.recovery.close", "tailscale.recovery.close"), dialog =>
                new TailscaleRecoveryDialog(preview, target, text, value => { accepted = value; dialog.Close(value); }), new Size(580, 380));
            return accepted;
        };
        model.EditPreferencesDialogAsync = async (preferences, exitNodes, platform, peersRestricted) =>
        {
            TailscalePreferences? result = null;
            await window.ShowDialogAsync<TailscalePreferences?>(text.Get("tailscale.preferences.title", "tailscale.preferences.title"), dialog =>
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
            await window.ShowDialogAsync<bool>(text.Get("tailscale.about.title", "tailscale.about.title"), dialog =>
                new TailscaleAboutDialog(new TailscaleAboutText(text, Manifest.Version, model.Target, model.HostVersion,
                    model.DeviceName, model.TailnetDisplay, model.AccountLogin), () => dialog.Close(true)), new Size(520, 360));
        };

    }
}
