using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed record ProjectDraftPrompt(IReadOnlyList<string> Paths);

internal partial class ProjectDraftDialog : UserControl
{
    private readonly Action<EditorDraftAction> _close;
    public ProjectDraftDialog(IReadOnlyList<string> paths, Action<EditorDraftAction> close)
    {
        _close = close;
        InitializeComponent();
        DataContext = new ProjectDraftPrompt(paths);
    }
    private void Save_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Save);
    private void Discard_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Discard);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _close(EditorDraftAction.Cancel);
}
