using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesPasswordDialogView : UserControl
{
    private readonly ModalDialog<string?> _dialog;
    public FileServicesPasswordDialogView(ModalDialog<string?> dialog, string? message)
    {
        _dialog = dialog;
        InitializeComponent();
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) { PasswordBox.Text = null; _dialog.Cancel(); }
    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text;
        PasswordBox.Text = null;
        _dialog.Close(password);
    }
}
