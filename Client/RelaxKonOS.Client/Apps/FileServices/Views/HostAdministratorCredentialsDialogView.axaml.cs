using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class HostAdministratorCredentialsDialogView : UserControl
{
    private readonly ModalDialog<HostAdministratorCredentials?> _dialog;
    private readonly string _defaultAdministrator;

    public HostAdministratorCredentialsDialogView(ModalDialog<HostAdministratorCredentials?> dialog, string? message,
        string? errorMessage, string defaultAdministrator)
    {
        _dialog = dialog;
        _defaultAdministrator = defaultAdministrator;
        InitializeComponent();
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);
        ErrorText.Text = errorMessage;
        ErrorText.IsVisible = !string.IsNullOrEmpty(errorMessage);
        AccountBox.Text = defaultAdministrator;
        AccountBox.PlaceholderText = LocalizedText.Get("file_services.host_account");
        PasswordBox.PlaceholderText = LocalizedText.Get("settings.host_time.password");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = null;
        _dialog.Cancel();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? string.Empty;
        PasswordBox.Text = null;
        var account = AccountBox.Text?.Trim();
        _dialog.Close(new HostAdministratorCredentials(string.IsNullOrWhiteSpace(account) ? _defaultAdministrator : account, password));
    }
}
