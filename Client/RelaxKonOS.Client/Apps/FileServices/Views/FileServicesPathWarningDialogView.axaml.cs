using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesPathWarningDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;
    public FileServicesPathWarningDialogView(string path, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        Message.Text = LocalizedText.Format("file_services.path_warning_message", path);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private void Continue_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
