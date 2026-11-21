using Avalonia.Controls;

namespace RelaxKonOS.WindowManager;

/// <summary>Temporarily attaches a workspace editor to an owned dialog while preserving its draft.</summary>
public static class WorkspaceEditorContent
{
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

