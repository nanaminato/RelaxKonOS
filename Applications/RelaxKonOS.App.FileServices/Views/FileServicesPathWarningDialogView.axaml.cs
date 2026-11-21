using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.FileServices.Views;

public partial class FileServicesPathWarningDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;
    public FileServicesPathWarningDialogView(string path, ModalDialog<bool> dialog, Func<string, string> localize)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = new DialogLabels(localize);
        Message.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, localize("file_services.path_warning_message"), path);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private void Continue_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
