using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.FileServices.Views;

public partial class FileServicesDeleteDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;
    public FileServicesDeleteDialogView(string name, ModalDialog<bool> dialog, Func<string, string> localize)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = new DialogLabels(localize);
        Message.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, localize("file_services.delete_confirm"), name);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private void Delete_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
