using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.ViewModels.ServerCenter;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

public partial class ServerCenterManagementDialog : UserControl
{
    private readonly Action _close;

    public ServerCenterManagementDialog(ServerCenterViewModel viewModel, string section, Action close)
    {
        InitializeComponent();
        DataContext = viewModel;
        _close = close;
        DetailsPanel.IsVisible = section == "details";
        RepairPanel.IsVisible = RepairButton.IsVisible = section == "repair";
        UninstallPanel.IsVisible = UninstallButton.IsVisible = section == "uninstall";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ServerCenterViewModel { IsBusy: false }) _close();
    }
}
