using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Client.Services.Dialogs;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

public partial class ServerCenterManagementDialog : UserControl
{
    private readonly Action _close;

    public ServerCenterManagementDialog(ServerCenterViewModel viewModel, string section, ModalDialog<bool> dialog)
    {
        InitializeComponent();
        DataContext = viewModel;
        _close = dialog.Cancel;
        var configuration = (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian);
        var repair = (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities);
        var uninstall = (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData);
        DraftDialogGuard.Attach(dialog, () => viewModel.IsBusy,
            () => section is "settings" or "components"
                ? configuration != (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian)
                : section == "repair"
                ? repair != (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities)
                : section == "uninstall" && uninstall != (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData));
        ConfigurationPanel.IsVisible = ConfigureButton.IsVisible = section is "settings" or "components";
        NetworkSettingsPanel.IsVisible = section == "settings";
        GuardianChoice.IsVisible = section == "components" && viewModel.CanConfigureGuardian;
        DetailsPanel.IsVisible = section is "details" or "repair";
        RepairPanel.IsVisible = RepairButton.IsVisible = section == "repair";
        UninstallPanel.IsVisible = UninstallButton.IsVisible = section == "uninstall";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ServerCenterViewModel { IsBusy: false }) _close();
    }
}
