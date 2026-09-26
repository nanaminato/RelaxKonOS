using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Views.Login;

internal partial class SshHostKeyDialog : Window
{
    public SshHostKeyDialog(string title, string message, string fingerprint, string confirmText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        FingerprintText.Text = fingerprint;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
