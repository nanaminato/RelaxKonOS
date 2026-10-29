using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Controls;

/// <summary>One modal editor at a time; fields retain their draft and never acquire a second parent.</summary>
public partial class WorkspaceEditorDialogView : UserControl
{
    private readonly ModalDialog<bool>? _dialog;

    public WorkspaceEditorDialogView() => InitializeComponent();

    public WorkspaceEditorDialogView(Control editor, ModalDialog<bool> dialog) : this()
    {
        _dialog = dialog;
        DataContext = editor.DataContext;
        EditorHost.Content = editor;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog?.Cancel();

    public static async Task PresentAsync(AppContext context, ManagedWindow owner, string title, Control editor, Func<bool> busy, Action<ModalDialog<bool>?> activeChanged)
    {
        WorkspaceEditorDialogView? content = null;
        try
        {
            await context.ShowDialogAsync<bool>(owner, title, dialog =>
            {
                activeChanged(dialog);
                dialog.CanCancelAsync = () => Task.FromResult(!busy());
                return content = new WorkspaceEditorDialogView(editor, dialog);
            }, new RelaxKonOS.Core.Primitives.Size(720, 650));
        }
        finally { activeChanged(null); if (content is not null) content.EditorHost.Content = null; }
    }

    public static async Task OpenAsync(UserControl workspace, string name, string title, Func<string, Control, Task> presenter)
    {
        if (workspace.FindControl<StackPanel>(name) is not { Parent: Panel parent } editor) return;
        var index = parent.Children.IndexOf(editor);
        editor.DataContext = workspace.DataContext;
        parent.Children.Remove(editor);
        editor.IsVisible = true;
        try { await presenter(title, editor); }
        finally
        {
            editor.IsVisible = false;
            parent.Children.Insert(Math.Min(index, parent.Children.Count), editor);
        }
    }
}
