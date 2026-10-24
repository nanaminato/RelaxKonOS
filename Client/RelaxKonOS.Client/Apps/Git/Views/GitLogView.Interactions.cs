using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitLogView
{
    public void CopyPresentationFrom(GitLogView source)
    {
        _branchWidth = source.LogLayoutRoot.ColumnDefinitions[0].Width.Value > 0 ? source.LogLayoutRoot.ColumnDefinitions[0].Width.Value : source._branchWidth;
        _filesWidth = source.LogLayoutRoot.ColumnDefinitions[4].Width.Value;
        _showBranches = source._showBranches; _singleClickFilters = source._singleClickFilters;
        _favorites.UnionWith(source._favorites);
        AuthorsOption.IsChecked = source.AuthorsOption.IsChecked; DatesOption.IsChecked = source.DatesOption.IsChecked; ReferencesOption.IsChecked = source.ReferencesOption.IsChecked;
        DiffPreviewToggle.IsChecked = source.DiffPreviewToggle.IsChecked;
    }
    private void RestoreBranchSelection()
    {
        var selected = _selectedBranchName ?? _vm?.SelectedLogBranch?.Value;
        IEnumerable<BranchTreeNode> Flatten(IEnumerable<BranchTreeNode> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
        var match = string.IsNullOrEmpty(selected) ? null : Flatten(BranchTreeRoots).FirstOrDefault(node => node.Kind != BranchNodeKind.Head && node.Branch?.Name == selected);
        var wasRebuilding = _rebuildingBranches; _rebuildingBranches = true;
        try { BranchTree.SelectedItem = match; }
        finally { _rebuildingBranches = wasRebuilding; }
    }
    private bool _branchRebuildPending;
    private void BranchesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueLogRows();
        if (_branchRebuildPending) return;
        _branchRebuildPending = true;
        Dispatcher.UIThread.Post(() => { _branchRebuildPending = false; if (_attached) RebuildBranchTree(); });
    }
    private void FilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueCommitFileTreeRebuild();
    private void CommitsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueLogRows();
    private void QueueLogRows()
    {
        if (_rowsPending) return; _rowsPending = true;
        Dispatcher.UIThread.Post(() => { _rowsPending = false; if (_attached) RebuildLogRows(); });
    }
    private void UpdateActionButtons()
    {
        DeleteBranchButton.IsEnabled = _vm?.CanManage == true && _vm.SelectedBranch is { IsRemote: false, IsCurrent: false };
        CompareBranchButton.IsEnabled = _vm?.CanManage == true && _vm.SelectedBranch is not null;
        FavoriteBranchButton.IsEnabled = _vm?.SelectedBranch is not null;
        if (_vm is null) { CherryPickButton.IsEnabled = false; return; }
        var parents = _vm.GraphCommits.ToDictionary(item => item.Sha, item => item.Parents);
        var reachable = new HashSet<string>(); var pending = new Stack<string>(_vm.GraphCommits.Where(item => item.IsHead).Select(item => item.Sha));
        while (pending.TryPop(out var sha)) if (reachable.Add(sha) && parents.TryGetValue(sha, out var ancestors)) foreach (var parent in ancestors) pending.Push(parent);
        CherryPickButton.IsEnabled = _vm.CanManage && _vm.SelectedCommit is { } commit && !reachable.Contains(commit.Sha);
    }
    private void RebuildLogRows()
    {
        if (_vm is null) return;
        var scroll = CommitList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        _rebuildingRows = true;
        try
        {
            LogRows.Clear();
            foreach (var row in GitCommitGraph.Build(_vm.Commits.ToArray(), _vm.GraphCommits.ToArray(), _vm.Branches.ToArray(), _vm.Tags.ToArray())) LogRows.Add(row);
            GraphWidth = Math.Clamp(LogRows.Select(row => row.Graph.LaneCount).DefaultIfEmpty(1).Max() * 13 + 12, 24, 180);
            CommitList.SelectedItem = LogRows.FirstOrDefault(row => row.Commit.Sha == _vm.SelectedCommit?.Sha);
        }
        finally { _rebuildingRows = false; }
        UpdateActionButtons();
        if (scroll is not null && offset is { } oldOffset) Dispatcher.UIThread.Post(() => { if (_attached) scroll.Offset = oldOffset; }, DispatcherPriority.Loaded);
    }
    private void CommitList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingRows && _vm is not null) _vm.SelectedCommit = (CommitList.SelectedItem as GitLogRow)?.Commit;
    }
    private void HideBranches_Click(object? sender, RoutedEventArgs e) => SetBranchesVisible(false);
    private void ShowBranches_Click(object? sender, RoutedEventArgs e) => SetBranchesVisible(true);
    private void FavoriteBranch_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedBranch is not { } branch) return;
        if (!_favorites.Add(branch.Name)) _favorites.Remove(branch.Name); RebuildBranchTree();
    }
    private async void CompareSelectedBranch_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedBranch is { } branch && _vm.CanManage) await _vm.CompareBranchCommand.ExecuteAsync(branch);
    }
    private void BranchOptions_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var singleClick = new CheckBox { Content = LocalizedText.Get("git.dock.single_click_filter"), IsChecked = _singleClickFilters };
        singleClick.IsCheckedChanged += (_, _) => _singleClickFilters = singleClick.IsChecked == true;
        new Flyout { Content = singleClick }.ShowAt(anchor);
    }
    private static void ExpandBranches(IEnumerable<BranchTreeNode> nodes, bool expanded)
    { foreach (var node in nodes) { node.IsExpanded = expanded; ExpandBranches(node.Children, expanded); } }
    private void ExpandBranches_Click(object? sender, RoutedEventArgs e) => ExpandBranches(BranchTreeRoots, true);
    private void CollapseBranches_Click(object? sender, RoutedEventArgs e) => ExpandBranches(BranchTreeRoots, false);
    private static void ExpandFiles(IEnumerable<CommitFileTreeNode> nodes, bool expanded)
    { foreach (var node in nodes) { node.IsExpanded = expanded; ExpandFiles(node.Children, expanded); } }
    private void ExpandFiles_Click(object? sender, RoutedEventArgs e) => ExpandFiles(CommitFileTreeRoots, true);
    private void CollapseFiles_Click(object? sender, RoutedEventArgs e) => ExpandFiles(CommitFileTreeRoots, false);
    private async void ChangedFileTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.SelectedCommitFile is { } file) { await _vm.ShowCommitFileDiffCommand.ExecuteAsync(file); e.Handled = true; }
    }
    private async void PreviewDiff_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (DiffPreviewToggle.IsChecked == true && _vm.SelectedCommitFile is { } file) await _vm.PreviewLogFileDiffAsync(file);
        else if (DiffPreviewToggle.IsChecked != true) _vm.CloseInlineDiffCommand.Execute(null);
    }
    private void Presentation_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ShowAuthors = AuthorsOption.IsChecked == true; ShowDates = DatesOption.IsChecked == true; ShowReferences = ReferencesOption.IsChecked == true;
        ApplyResponsiveLayout();
    }
    private void ClearPathFilter_Click(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.LogPathFilter = ""; }
    private async void CommitSearch_KeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && _vm is not null) { await _vm.ApplyLogFiltersCommand.ExecuteAsync(null); e.Handled = true; } }
}
