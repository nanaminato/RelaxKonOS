using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.ProxyServer.Views;

public partial class ProxyServerEditorDialog : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public ProxyServerEditorDialog() => InitializeComponent();

    public ProxyServerEditorDialog(Control editor, ModalDialog<bool> dialog) : this()
    {
        _dialog = dialog;
        DataContext = editor.DataContext;
        EditorHost.Content = editor;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog?.Cancel();

    public void DetachEditor() => EditorHost.Content = null;
}
