using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesDeleteDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;
    public FileServicesDeleteDialogView(string name, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        Message.Text = RelaxKonOS.Client.Localization.LocalizedText.Format("file_services.delete_confirm", name);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private void Delete_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
