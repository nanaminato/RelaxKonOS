using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Docker.Views;

internal partial class DockerErrorDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public DockerErrorDialogView(string message, ModalDialog<bool> dialog)
    {
        Message = message;
        _dialog = dialog;
        InitializeComponent();
        DataContext = this;
    }

    public string Message { get; }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);
}
