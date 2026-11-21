using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases.Views;

public partial class DatabaseConfirmDialog : UserControl
{
    private readonly Action<bool> _complete;

    public DatabaseConfirmDialog(ISystemLanguage language, string messageKey, string? detail, Action<bool> complete)
    {
        _complete = complete;
        InitializeComponent();
        DatabaseDialogLanguage.Bind(this, language, text =>
        {
            Message.Text = text(messageKey) + (detail is null ? "" : "\n\n" + detail);
            CancelButton.Content = text("database.dialog_cancel");
            ConfirmButton.Content = text("database.dialog_ok");
        });
    }

    private void Accept(object? sender, RoutedEventArgs args) => _complete(true);
    private void Cancel(object? sender, RoutedEventArgs args) => _complete(false);
}
