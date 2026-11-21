using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Squid.Views;

public partial class SquidEditorDialog : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public SquidEditorDialog() => InitializeComponent();

    public SquidEditorDialog(Control editor, ModalDialog<bool> dialog) : this()
    {
        _dialog = dialog;
        DataContext = editor.DataContext;
        EditorHost.Content = editor;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog?.Cancel();

    public void DetachEditor() => EditorHost.Content = null;
}
