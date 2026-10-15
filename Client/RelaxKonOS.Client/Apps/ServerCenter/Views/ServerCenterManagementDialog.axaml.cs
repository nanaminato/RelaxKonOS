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
        var configuration = (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian, viewModel.ConfigurationAutoStart, viewModel.ConfigurationFileAccess?.Scope, viewModel.ConfigurationFileRoots);
        var repair = (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities, viewModel.RepairPackagePath);
        var uninstall = (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData);
        DraftDialogGuard.Attach(dialog, () => viewModel.IsBusy,
            () => section is "settings" or "components"
                ? configuration != (viewModel.ConfigurationPort, viewModel.ConfigurationLan, viewModel.ConfigurationGuardian, viewModel.ConfigurationAutoStart, viewModel.ConfigurationFileAccess?.Scope, viewModel.ConfigurationFileRoots)
                : section == "repair"
                ? repair != (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities, viewModel.RepairPackagePath)
                : section == "uninstall" && uninstall != (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData));
        ConfigurationPanel.IsVisible = ConfigureButton.IsVisible = section is "settings" or "components";
        NetworkSettingsCard.IsVisible = NetworkSettingsPanel.IsVisible = section == "settings";
        GuardianChoice.IsVisible = section == "components" && viewModel.CanConfigureGuardian;
        DetailsPanelCard.IsVisible = DetailsPanel.IsVisible = section == "details";
        GuardianCard.IsVisible = section == "components";
        RepairPanelCard.IsVisible = RepairPanel.IsVisible = RepairButton.IsVisible = section == "repair";
        UninstallPanelCard.IsVisible = UninstallPanel.IsVisible = UninstallButton.IsVisible = section == "uninstall";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ServerCenterViewModel { IsBusy: false }) _close();
    }
}
