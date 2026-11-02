using Avalonia.Controls;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Firewall.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>Built-in host firewall editor (UFW on Linux, Defender Firewall on Windows).</summary>
public sealed class FirewallApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.firewall"), "Firewall", "0.3.0", "🧱", "Manage host firewall policies and rules",
        [AppPermissions.ServerFirewallRead, AppPermissions.ServerFirewallManage],
        ServerRequirements: new ApplicationServerRequirements(Platforms: [ApplicationPlatformNames.Linux, ApplicationPlatformNames.Windows], Capabilities: [ServerCapabilities.Firewall]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFirewallClient)) as IRemoteFirewallClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.firewall.display_name"),
                new TextBlock { Text = LocalizedText.Get("firewall.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var viewModel = new FirewallViewModel(client, context.Permissions);
        var view = new FirewallWorkspace(viewModel);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.firewall.display_name"), view, new Rect(70, 55, 1120, 780), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        viewModel.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        viewModel.ShowRuleEditorAsync = editing => ShowRuleEditorAsync(context, window, viewModel, editing);
        viewModel.ConfirmAsync = (title, message) => ConfirmAsync(context, window, title, message);
        _ = viewModel.StartAsync();
    }

    /// <summary>Used by the window-preview checks to render the real workspace without a window host.</summary>
    internal static Control CreateView(FirewallViewModel viewModel) => new FirewallWorkspace(viewModel);

    private static Task ShowRuleEditorAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, FirewallViewModel viewModel, bool editing) =>
        context.ShowDialogAsync<bool>(owner,
            LocalizedText.Get(editing ? "firewall.rule.edit_dialog_title" : "firewall.rule.add_dialog_title"), dialog =>
            {
                var original = viewModel.DraftSnapshot();
                DraftDialogGuard.Attach(dialog, () => viewModel.IsLoading, () => original != viewModel.DraftSnapshot());
                return new FirewallRuleEditorView(viewModel, dialog);
            }, new RelaxKonOS.Core.Primitives.Size(520, 620));

    /// <summary>A host change asks first. The dialog stays owned by the firewall window so it can
    /// never land behind another application.</summary>
    private static async Task<bool> ConfirmAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, string title, string message)
    {
        var accepted = false;
        await context.ShowDialogAsync<bool>(owner, title, dialog => new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(message, value =>
            {
                accepted = value;
                dialog.Close(value);
            }),
        }, new RelaxKonOS.Core.Primitives.Size(520, 280));
        return accepted;
    }
}
