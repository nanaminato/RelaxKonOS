using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxySubscriptionContentDialogView : UserControl
{
    private readonly ModalDialog<bool> dialog;

    public ProxySubscriptionContentDialogView(ProxyManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        this.dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => dialog.Close(true);
}
