using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.App.PortForwarding.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.PortForwarding;

public sealed class PortForwardingPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.port-forwarding"), "Port Forwarding", "1.0.0", "↔",
        "Local SSH forwards to remote loopback services", [AppPermissions.DesktopPortForwardingManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => PortForwardingLocalization.Resolve(language, key, key);
        var permissions = context.Permissions;
        var permission = AppPermissions.DesktopPortForwardingManage;
        var service = context.PortForwarding;
        if (service is not null && permissions.GetStatus(permission) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(permission, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        const string titleKey = "application.relaxkonos.port-forwarding.display_name";
        if (service is null || !permissions.IsGranted(permission))
        {
            context.Windows.ShowWindow(Label(titleKey), new TextBlock
            {
                Text = Label("port_forwarding.permission.required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            }, new Rect(160, 100, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var model = new PortForwardingViewModel(service, Label);
        var view = new PortForwardingMainView { DataContext = model };
        var window = context.Windows.ShowWindow(Label(titleKey), view, new Rect(160, 100, 760, 650), Manifest.IconGlyph);
        view.AttachWindowHeader(window.Window);

        model.ShowForwardEditorAsync = async forward =>
        {
            try
            {
                await window.ShowDialogAsync<bool>(Label(forward is null ? "port_forwarding.new_forward" : "port_forwarding.edit"), dialog =>
                {
                    model.CloseForwardEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return new PortForwardingEditorDialogView(model, dialog, forward is not null, (owner, busy, changed) =>
                    {
                        owner.CanCancelAsync = async () =>
                        {
                            if (busy()) return false;
                            if (!changed()) return true;
                            var discard = await window.ConfirmNestedAsync(owner, Label("common.discard_title"),
                                Label("common.discard_message"), Label("common.discard"));
                            return discard && !busy();
                        };
                    });
                }, new Size(480, 360));
            }
            finally
            {
                model.CloseForwardEditorAsync = null;
                model.SshPassword = string.Empty;
            }
        };

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (window.Closed.IsCancellationRequested) return;
                window.Window.Title = Label(titleKey);
                model.OnLanguageChanged(sender, args);
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += LanguageChanged;
        window.Closed.Register(() =>
        {
            language.LanguageChanged -= LanguageChanged;
            if (Dispatcher.UIThread.CheckAccess()) model.Dispose();
            else Dispatcher.UIThread.Post(model.Dispose);
        });
    }
}
