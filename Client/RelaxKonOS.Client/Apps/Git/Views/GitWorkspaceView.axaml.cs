using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitWorkspaceView : UserControl
{
    private GitClientViewModel? _vm;

    public GitWorkspaceView() => InitializeComponent();
    public GitWorkspaceView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        AttachCollectionHandlers();
        UpdateGroupVisibility();
    }

    private void AttachCollectionHandlers()
    {
        if (_vm is null) return;

        _vm.TrackedChanges.CollectionChanged += (s, e) => UpdateGroupVisibility();
        _vm.UntrackedChanges.CollectionChanged += (s, e) => UpdateGroupVisibility();
        _vm.Changes.CollectionChanged += (s, e) => UpdateGroupVisibility();
    }

    private void UpdateGroupVisibility()
    {
        if (_vm is null) return;

        TrackedSection.IsVisible = _vm.TrackedChanges.Count > 0;
        UntrackedSection.IsVisible = _vm.UntrackedChanges.Count > 0;
        EmptyState.IsVisible = _vm.Changes.Count == 0;

        TrackedCountText.Text = LocalizedText.Format("git.workspace.file_count_short_format", _vm.TrackedChanges.Count);
        UntrackedCountText.Text = LocalizedText.Format("git.workspace.file_count_short_format", _vm.UntrackedChanges.Count);
    }

    private void StageFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitFileChangeDto file } && _vm is not null)
            _vm.StageFileCommand.Execute(file);
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.SelectAllChangesCommand.Execute(null);
    }

    private void Commit_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.SelectedCount == 0)
        {
            _vm.StatusText = LocalizedText.Get("git.status.no_files_selected");
            return;
        }
        if (string.IsNullOrWhiteSpace(_vm.CommitMessage))
        {
            _vm.StatusText = LocalizedText.Get("git.status.commit_message_required");
            return;
        }
        _vm.CommitCommand.Execute(null);
    }

    private void CommitAndPush_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.SelectedCount == 0)
        {
            _vm.StatusText = LocalizedText.Get("git.status.no_files_selected");
            return;
        }
        if (string.IsNullOrWhiteSpace(_vm.CommitMessage))
        {
            _vm.StatusText = LocalizedText.Get("git.status.commit_message_required");
            return;
        }
        _vm.CommitAndPushCommand.Execute(null);
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.ClearSelectionCommand.Execute(null);
    }

    private void Change_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The checkbox remains dedicated to selection; clicking the file name opens its diff.
        if (e.GetCurrentPoint(sender as Control).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || sender is not Control { DataContext: GitFileChangeItem item })
            return;

        _vm?.ViewDiffCommand.Execute(item.File);
        e.Handled = true;
    }
}
