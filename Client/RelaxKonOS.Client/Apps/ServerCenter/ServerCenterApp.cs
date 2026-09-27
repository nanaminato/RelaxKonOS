using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>Server inventory available inside an SSH desktop.</summary>
public sealed class ServerCenterApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.server-center"),
        DisplayName: "Install or manage server",
        Version: "1.0.0",
        IconGlyph: "🖧",
        Description: "SSH hosts and RelaxKonOS installation",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var sshSession = context.Services.GetRequiredService<SshDesktopSession>();
        if (!sshSession.IsConnected) return;
        var viewModel = context.Services.GetRequiredService<ServerCenterViewModel>();
        var window = context.ShowWindow(viewModel.Title, new ServerCenterWorkspace { DataContext = viewModel },
            new Rect(70, 50, 1120, 760), Manifest.IconGlyph);
        viewModel.ShowInstallationWizardAsync = () => context.ShowDialogAsync<bool>(window, viewModel.DeployText,
            dialog => new ServerInstallationWizardView(
                new ServerInstallationWizardViewModel(viewModel, () => dialog.Close(true),
                    () => context.ShowDialogAsync<string?>(window,
                        viewModel.Text("server_center.wizard.choose_server_bundle", "Browse server files"),
                        picker => new SshFileBrowserView(sshSession, selectPackage: path => picker.Close(path),
                            cancelPicker: picker.Cancel),
                        new Size(860, 580)),
                    async () =>
                    {
                        var addresses = await viewModel.GetHostIpAddressesAsync();
                        if (addresses is null) return;
                        await context.ShowDialogAsync<bool>(window,
                            viewModel.Text("server_center.host_addresses_title", "Host IP addresses"),
                            addressDialog =>
                            {
                                var close = new Button { Content = viewModel.CloseText, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                                close.Click += (_, _) => addressDialog.Close(true);
                                var content = new StackPanel { Spacing = 14 };
                                content.Children.Add(new TextBlock
                                {
                                    Text = addresses.Count == 0
                                        ? viewModel.Text("server_center.host_addresses_empty", "No IP addresses were reported by this host.")
                                        : string.Join(Environment.NewLine, addresses),
                                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                                });
                                content.Children.Add(close);
                                return new Border
                                {
                                    Padding = new Thickness(22),
                                    Child = content
                                };
                            }, new Size(440, 280));
                    })),
            new Size(620, 480));
        _ = viewModel.LoadAsync();
    }
}
