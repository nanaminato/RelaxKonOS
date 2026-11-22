using RelaxKonOS.App.SshFiles.Views;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Core.Primitives;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.ServerCenter;

internal sealed class ServerCenterHostWorkspace(IServiceProvider services, IWindowManager windowManager)
    : IServerCenterPackageWorkspace
{
    public void Open(Func<IServerCenterWorkspacePresentation, Func<ServerCenterWorkspacePage, Control>, ManagedWindow> showWindow)
    {
        var sshSession = services.GetRequiredService<SshDesktopSession>();
        var language = services.GetRequiredService<ISystemLanguage>();
        UsageMemoryScope CapturePickerMemory() => UsageMemoryStore.CaptureServices(services);
        var viewModel = services.GetRequiredService<ServerCenterViewModel>();
        var window = showWindow(viewModel, CreatePage);
        viewModel.ConfirmRollbackAsync = review => windowManager.ShowDialogAsync<bool>(window, viewModel.RollbackText,
            dialog => new ServerRollbackConfirmationView(review, dialog), new Size(560, 420));
        viewModel.ShowManagementDialogAsync = async section =>
        {
            // Opening the dialog runs after the page probed the host over SSH, which takes a
            // network round trip. The user can close this workspace in the meantime (Alt+F4,
            // taskbar preview, window overview), so the captured window is re-checked here
            // instead of assuming the probe returned to a live desktop.
            if (!windowManager.Windows.Contains(window)) return;
            await windowManager.ShowDialogAsync<bool>(window,
                section switch { "components" => viewModel.ComponentsText, "settings" => viewModel.SettingsText, "repair" => viewModel.RepairText, "uninstall" => viewModel.UninstallText, _ => viewModel.InstallationInfoTitle },
                dialog => new ServerCenterManagementDialog(viewModel, section, dialog, language, CapturePickerMemory), new Size(640, 600));
        };
        viewModel.ShowInstallationWizardAsync = () => windowManager.ShowDialogAsync<bool>(window, viewModel.DeployText,
            dialog => new ServerInstallationWizardView(
                new ServerInstallationWizardViewModel(viewModel, () => dialog.Close(true),
                    async () => { await dialog.CancelAsync(); return dialog.Result.IsCompleted; },
                    () => dialog.ShowDialogAsync<string?>(
                        viewModel.Text("server_center.wizard.choose_server_bundle", "Browse server files"),
                        picker => new SshFileBrowserView(sshSession, language, new SshTransferPicker(CapturePickerMemory), selectPackage: path => picker.Close(path),
                            cancelPicker: picker.Cancel),
                        new Size(860, 580)),
                    async () =>
                    {
                        var addresses = await viewModel.GetHostIpAddressesAsync();
                        if (addresses is null) return;
                        await dialog.ShowDialogAsync<bool>(
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
                    }), dialog, CapturePickerMemory),
            new Size(620, 480));
        _ = viewModel.LoadAsync();
    }

    internal static Control CreatePage(ServerCenterWorkspacePage page) => page switch
    {
        ServerCenterWorkspacePage.Hosts => new ServerCenterHostsPage(),
        ServerCenterWorkspacePage.Deployment => new ServerCenterDeploymentPage(),
        ServerCenterWorkspacePage.History => new ServerCenterHistoryPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };
}
