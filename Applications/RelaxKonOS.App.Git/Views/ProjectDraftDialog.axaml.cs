using Avalonia.Controls;
using RelaxKonOS.App.Git;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Git.Views;

internal sealed record ProjectDraftPrompt(IReadOnlyList<string> Paths, Func<string, string> Labels);

public partial class ProjectDraftDialog : UserControl
{
    private readonly Action<EditorDraftAction> _close;
    public ProjectDraftDialog(IReadOnlyList<string> paths, Action<EditorDraftAction> close, Func<string, string> localize)
    {
        _close = close;
        InitializeComponent();
        DataContext = new ProjectDraftPrompt(paths, localize);
    }
    private void Save_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Save);
    private void Discard_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Discard);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Cancel);
}
