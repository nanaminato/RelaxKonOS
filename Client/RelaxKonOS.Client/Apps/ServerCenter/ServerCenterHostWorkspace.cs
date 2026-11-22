using RelaxKonOS.UI.ServerInstallation;
using RelaxKonOS.UI.SshFiles;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using RelaxKonOS.Client.Services.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Core.Primitives;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.ServerCenter;

internal sealed class ServerCenterHostWorkspace(IServiceProvider services, IWindowManager windowManager)
    : IServerCenterPackageWorkspace
{
    public void Open(Func<IServerCenterWorkspacePresentation, ManagedWindow> showWindow,
        Func<ServerRollbackReview, ModalDialog<bool>, Func<string, Binding>, Control> createRollbackConfirmation,
        ServerCenterManagementViewFactory createManagementView)
    {
        var sshSession = services.GetRequiredService<SshDesktopSession>();
        var language = services.GetRequiredService<ISystemLanguage>();
        UsageMemoryScope CapturePickerMemory() => UsageMemoryStore.CaptureServices(services);
        var viewModel = services.GetRequiredService<ServerCenterViewModel>();
        var window = showWindow(viewModel);
        viewModel.ConfirmRollbackAsync = review => windowManager.ShowDialogAsync<bool>(window, viewModel.RollbackText,
            dialog => createRollbackConfirmation(review, dialog,
                key => (Binding)new RelaxKonOS.Client.Localization.LocExtension(key).ProvideValue(null!)),
            new Size(560, 420));
        viewModel.ShowManagementDialogAsync = async section =>
        {
            // Opening the dialog runs after the page probed the host over SSH, which takes a
            // network round trip. The user can close this workspace in the meantime (Alt+F4,
            // taskbar preview, window overview), so the captured window is re-checked here
            // instead of assuming the probe returned to a live desktop.
            if (!windowManager.Windows.Contains(window)) return;
            await windowManager.ShowDialogAsync<bool>(window,
                section switch { "components" => viewModel.ComponentsText, "settings" => viewModel.SettingsText, "repair" => viewModel.RepairText, "uninstall" => viewModel.UninstallText, _ => viewModel.InstallationInfoTitle },
                dialog => CreateManagementDialog(viewModel, section, dialog, language, CapturePickerMemory, createManagementView),
                new Size(640, 600));
        };
        viewModel.ShowInstallationWizardAsync = () => windowManager.ShowDialogAsync<bool>(window, viewModel.DeployText,
            dialog => CreateInstallationWizard(dialog,
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
                    }), (storage, options, purpose) => UsageFilePicker.OpenFilePickerAsync(storage, options, CapturePickerMemory(), purpose)),
            new Size(620, 480));
        _ = viewModel.LoadAsync();
    }

    internal static Control CreateManagementDialog(ServerCenterViewModel viewModel, string section, ModalDialog<bool> dialog,
        ISystemLanguage language, Func<UsageMemoryScope> capturePickerMemory, ServerCenterManagementViewFactory createView)
    {
        var view = createView(viewModel, section, dialog.Cancel,
            () => viewModel.BrowseRepairPackageAsync(session => dialog.ShowDialogAsync<string?>(
                viewModel.BrowseRepairPackageText,
                picker => new SshFileBrowserView(session, language, new SshTransferPicker(capturePickerMemory),
                    selectPackage: path => picker.Close(path), cancelPicker: picker.Cancel),
                new RelaxKonOS.Core.Primitives.Size(860, 580))));
        var configuration = (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian, viewModel.ConfigurationAutoStart, viewModel.ConfigurationFileAccess?.Scope, viewModel.ConfigurationFileRoots);
        void AcceptConfiguration()
        {
            configuration = (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian, viewModel.ConfigurationAutoStart, viewModel.ConfigurationFileAccess?.Scope, viewModel.ConfigurationFileRoots);
        }
        if (section is "settings" or "components")
        {
            viewModel.ConfigurationApplied += AcceptConfiguration;
            view.DetachedFromVisualTree += (_, _) => viewModel.ConfigurationApplied -= AcceptConfiguration;
        }
        var repair = (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities, viewModel.RepairPackagePath);
        var uninstall = (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData);
        DraftDialogGuard.Attach(dialog, () => viewModel.IsBusy,
            () => section is "settings" or "components"
                ? configuration != (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian, viewModel.ConfigurationAutoStart, viewModel.ConfigurationFileAccess?.Scope, viewModel.ConfigurationFileRoots)
                : section == "repair"
                ? repair != (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities, viewModel.RepairPackagePath)
                : section == "uninstall" && uninstall != (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData));
        return view;
    }

    internal static ServerInstallationWizardView CreateInstallationWizard(ModalDialog<bool> dialog,
        ServerInstallationWizardViewModel model, ServerInstallationFilePicker pickFile)
    {
        DraftDialogGuard.Attach(dialog, () => model.IsBusy || model.Progress.IsBusy, () => model.HasDraftChanges);
        _ = ClearSecretsAfterCloseAsync(dialog, model);
        return new ServerInstallationWizardView(model, pickFile);
    }

    private static async Task ClearSecretsAfterCloseAsync(ModalDialog<bool> dialog, ServerInstallationWizardViewModel model)
    {
        await dialog.Result;
        await Dispatcher.UIThread.InvokeAsync(model.ClearSecrets);
    }

}
