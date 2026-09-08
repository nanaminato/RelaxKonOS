using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxyNetworkSettingsDialogView : UserControl
{
    private readonly ModalDialog<bool> dialog;
    private readonly ProxySettingsDto? settingsBeforeOpening;

    public ProxyNetworkSettingsDialogView(ProxyManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        this.dialog = dialog;
        settingsBeforeOpening = viewModel.CapturePendingSettings();
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProxyManagerViewModel viewModel) viewModel.RestorePendingSettings(settingsBeforeOpening);
        dialog.Cancel();
    }

    private async void Save_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProxyManagerViewModel viewModel && viewModel.SaveSettingsCommand.CanExecute(null))
            await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        dialog.Close(true);
    }
}
