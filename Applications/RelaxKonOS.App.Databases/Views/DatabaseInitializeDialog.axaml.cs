using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases.Views;

public partial class DatabaseInitializeDialog : UserControl
{
    private readonly Action<int> _complete;

    public DatabaseInitializeDialog(ISystemLanguage language, Action<int> complete)
    {
        _complete = complete;
        InitializeComponent();
        DatabaseDialogLanguage.Bind(this, language, text =>
        {
            Hint.Text = text("database.initialize_hint");
            PortLabel.Text = text("database.port");
            CancelButton.Content = text("database.dialog_cancel");
            ConfirmButton.Content = text("database.dialog_confirm");
        });
    }

    private void Accept(object? sender, RoutedEventArgs args)
    {
        if (Port.Value is >= 1024 and <= 65535)
            _complete((int)Port.Value.Value);
    }

    private void Cancel(object? sender, RoutedEventArgs args) => _complete(0);
}
