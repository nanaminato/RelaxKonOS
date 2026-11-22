using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ServerCenter.Views;

public partial class ServerCenterManagementDialog : UserControl
{
    private readonly Action _close;
    private readonly Func<Task> _browseRepairPackage;

    public ServerCenterManagementDialog(IServerCenterManagementPresentation presentation, string section,
        Action close, Func<Task> browseRepairPackage)
    {
        InitializeComponent();
        DataContext = presentation;
        _close = close;
        _browseRepairPackage = browseRepairPackage;
        ConfigurationPanel.IsVisible = ConfigureButton.IsVisible = section is "settings" or "components";
        NetworkSettingsCard.IsVisible = NetworkSettingsPanel.IsVisible = section == "settings";
        GuardianChoice.IsVisible = section == "components" && presentation.CanConfigureGuardian;
        DetailsPanelCard.IsVisible = DetailsPanel.IsVisible = section == "details";
        GuardianCard.IsVisible = section == "components";
        RepairPanelCard.IsVisible = RepairPanel.IsVisible = RepairButton.IsVisible = section == "repair";
        UninstallPanelCard.IsVisible = UninstallPanel.IsVisible = UninstallButton.IsVisible = section == "uninstall";
    }

    private async void BrowseRepairPackage_Click(object? sender, RoutedEventArgs e) =>
        await _browseRepairPackage();

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is IServerCenterManagementPresentation { IsBusy: false }) _close();
    }
}
