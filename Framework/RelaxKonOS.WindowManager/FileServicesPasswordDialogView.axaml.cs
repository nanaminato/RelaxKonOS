using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.WindowManager;

public partial class FileServicesPasswordDialogView : UserControl
{
    private readonly ModalDialog<string?> _dialog;
    public FileServicesPasswordDialogView(ModalDialog<string?> dialog, Func<string, string> localize, string? message, string? errorMessage = null)
    {
        _dialog = dialog;
        InitializeComponent();
        CancelButton.Content = localize("common.cancel");
        ConfirmButton.Content = localize("file_services.confirm");
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);
        ErrorText.Text = errorMessage;
        ErrorText.IsVisible = !string.IsNullOrEmpty(errorMessage);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) { PasswordBox.Text = null; _dialog.Cancel(); }
    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text;
        PasswordBox.Text = null;
        _dialog.Close(password);
    }
}
