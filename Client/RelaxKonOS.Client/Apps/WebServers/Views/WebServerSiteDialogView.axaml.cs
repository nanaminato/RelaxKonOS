using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

internal partial class WebServerSiteDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public WebServerSiteDialogView(WebServerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();

    private void RemoveBinding_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WebServerSiteBindingEditor binding }
            && DataContext is WebServerManagerViewModel viewModel)
            viewModel.RemoveSiteBindingCommand.Execute(binding);
    }
}
