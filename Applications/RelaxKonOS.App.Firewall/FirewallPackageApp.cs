using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.Firewall.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.Firewall;

public sealed class FirewallPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.firewall.display_name";
    private const string HelperUnavailableKey = "common.problem.privileged_helper_unavailable";

    public ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.firewall"), "Firewall", "1.0.0", "🧱", "Manage host firewall policies and rules",
        [AppPermissions.ServerFirewallRead, AppPermissions.ServerFirewallManage],
        ServerRequirements: new ApplicationServerRequirements(
            Platforms: [ApplicationPlatformNames.Linux, ApplicationPlatformNames.Windows], Capabilities: [ServerCapabilities.Firewall]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Localize(string key, string fallback) => FirewallLocalization.Resolve(language, key, fallback);
        string Label(string key) => Localize(key, key);

        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent)
        {
            context.Windows.ShowWindow(Label(TitleKey),
                new TextBlock { Text = Label("firewall.login_required"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerFirewallRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerFirewallRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerFirewallRead)
            && permissions.GetStatus(AppPermissions.ServerFirewallManage) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerFirewallManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var model = new FirewallViewModel(context.Firewall, permissions, Localize,
            (code, fallback) => IsHelperUnavailable(code) ? Label(HelperUnavailableKey) : code ?? fallback,
            IsHelperUnavailable);
        var view = new FirewallWorkspace(model);
        var owner = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(70, 55, 1120, 780), Manifest.IconGlyph);
        view.AttachWindowHeader(owner.Window);

        model.ConfirmAsync = (title, message) => owner.ShowDialogAsync<bool>(title,
            dialog => Notice(dialog, message, Label("common.ok"), Label("common.cancel")), new Size(520, 280));
        model.ShowPrivilegedHelperUnavailableAsync = _ => owner.ShowDialogAsync<bool>(Label(HelperUnavailableKey + ".title"),
            dialog => Notice(dialog, Label(HelperUnavailableKey), Label("common.ok")), new Size(560, 270));
        model.ShowRuleEditorAsync = editing => owner.ShowDialogAsync<bool>(
            Label(editing ? "firewall.rule.edit_dialog_title" : "firewall.rule.add_dialog_title"), dialog =>
            {
                var original = model.DraftSnapshot();
                dialog.CanCancelAsync = async () =>
                {
                    if (model.IsLoading) return false;
                    if (original == model.DraftSnapshot()) return true;
                    var discard = await dialog.ShowDialogAsync<bool>(Label("common.discard_title"),
                        confirmation => Notice(confirmation, Label("common.discard_message"),
                            Label("common.discard"), Label("common.cancel")));
                    return discard && !model.IsLoading;
                };
                return new FirewallRuleEditorView(model, dialog);
            }, new Size(520, 620));

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (owner.Closed.IsCancellationRequested) return;
                owner.Window.Title = Label(TitleKey);
                model.OnLanguageChanged(sender, args);
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += LanguageChanged;
        owner.Closed.Register(() => language.LanguageChanged -= LanguageChanged);
        await model.StartAsync();
    }

    internal static bool IsHelperUnavailable(string? code) =>
        code is not null && (code.EndsWith("/privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || code.Equals("privileged-helper-unavailable", StringComparison.OrdinalIgnoreCase)
            || code.Equals("firewall.privileged_proxy_required", StringComparison.OrdinalIgnoreCase));

    private static Control Notice(ModalDialog<bool> dialog, string message, string accept, string? cancel = null)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel };
            cancelButton.Click += (_, _) => dialog.Cancel();
            actions.Children.Add(cancelButton);
        }

        var acceptButton = new Button { Content = accept, Classes = { "primary" } };
        acceptButton.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(acceptButton);
        DockPanel.SetDock(actions, Dock.Bottom);
        return new DockPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                actions,
                new ScrollViewer
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
    }
}
