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
        var repair = (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities);
        var uninstall = (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData);
        DraftDialogGuard.Attach(dialog, () => viewModel.IsBusy,
            () => section == "repair"
                ? repair != (viewModel.MaintenanceAddFirewallRule, viewModel.RepairLanCertificate, viewModel.RepairCertificateIdentities)
                : section == "uninstall" && uninstall != (viewModel.RemoveSmb, viewModel.RemoveNginx, viewModel.RemoveFrp, viewModel.RemoveMihomo, viewModel.DeleteServerData));
        DetailsPanel.IsVisible = section == "details";
        RepairPanel.IsVisible = RepairButton.IsVisible = section == "repair";
        UninstallPanel.IsVisible = UninstallButton.IsVisible = section == "uninstall";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ServerCenterViewModel { IsBusy: false }) _close();
    }
}
