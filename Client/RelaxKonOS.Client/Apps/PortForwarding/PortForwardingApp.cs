using Avalonia.Utilities;
using RelaxKonOS.Client.Services;
using RelaxKonOS.App.PortForwarding;
using RelaxKonOS.App.PortForwarding.Views;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.PortForwarding;

/// <summary>Built-in, host-local SSH port forward manager.</summary>
public sealed class PortForwardingApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.port-forwarding"),
        DisplayName: "Port Forwarding",
        Version: "1.0.0",
        IconGlyph: "↔",
        Description: "Local SSH tunnels to server loopback services",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var localization = context.Services.GetRequiredService<LocalizationService>();
        string Label(string key) => PortForwardingLocalization.Resolve(localization, key, key);
        var viewModel = new PortForwardingViewModel(context.Services.GetRequiredService<IPortForwardingService>(), Label);
        WeakEventHandlerManager.Subscribe<LocalizationService, SystemLanguageChangedEventArgs, PortForwardingViewModel>(
            localization, nameof(LocalizationService.LanguageChanged), viewModel.OnLanguageChanged);
        var view = new PortForwardingMainView { DataContext = viewModel };
        var window = context.ShowWindow(Label("application.relaxkonos.port-forwarding.display_name"), view,
            bounds: new Rect(160, 100, 760, 650), iconGlyph: Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        viewModel.ShowForwardEditorAsync = async forward =>
        {
            try
            {
                await context.ShowDialogAsync<bool>(window,
                    Label(forward is null ? "port_forwarding.new_forward" : "port_forwarding.edit"), dialog =>
                    {
                        viewModel.CloseForwardEditorAsync = () =>
                        {
                            dialog.Close(true);
                            return Task.CompletedTask;
                        };
                        return new PortForwardingEditorDialogView(viewModel, dialog, forward is not null, RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach);
                    }, new Size(480, 360));
            }
            finally
            {
                viewModel.CloseForwardEditorAsync = null;
                viewModel.SshPassword = string.Empty;
            }
        };
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
    }
}
