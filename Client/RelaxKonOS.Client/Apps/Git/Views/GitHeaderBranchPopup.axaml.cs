using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed class GitHeaderBranchNode(string name, GitBranchDto? branch = null, bool tag = false)
{
    public string Name { get; } = name;
    public GitBranchDto? Branch { get; } = branch;
    public bool IsTag { get; } = tag;
    public bool IsGroup => Branch is null;
    public string? Tracking => Branch?.Tracking;
    public string Icon { get; set; } = branch is null ? "Folder" : tag || branch.IsCurrent ? "Tag" : "Branch";
    public ObservableCollection<GitHeaderBranchNode> Children { get; } = [];
}

internal partial class GitHeaderBranchPopup : UserControl
{
    private GitClientViewModel? _vm;
    private Action? _close;
    private Action<string>? _openTool;
    private Action? _commit;
    private readonly Dictionary<string, List<string>> _recent = [];
    private readonly Dictionary<string, HashSet<string>> _favorites = [];
    private int _refreshVersion;
    public void EndSession() => _refreshVersion++;
    public ObservableCollection<GitHeaderBranchNode> Nodes { get; } = [];
    public GitHeaderBranchPopup() { InitializeComponent(); DataContext = this; }
    public void Configure(GitClientViewModel vm, Action close, Action<string> openTool, Action commit)
    { _vm = vm; _close = close; _openTool = openTool; _commit = commit; }
    public void RecordCurrentBranch()
    {
        if (_vm?.SelectedRepository is not { } repo || _vm.Status is not { IsDetached: false } status) return;
        if (!_recent.TryGetValue(repo.Id, out var names)) _recent[repo.Id] = names = [];
        names.Remove(status.Branch); names.Insert(0, status.Branch);
        if (names.Count > 8) names.RemoveRange(8, names.Count - 8);
    }
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        BranchSearch.Text = "";
        RecordCurrentBranch(); Rebuild();
        if (_vm is not null) await _vm.RefreshBranchReferencesAsync();
        if (version != _refreshVersion) return;
        Rebuild();
        Dispatcher.UIThread.Post(() => BranchSearch.Focus(), DispatcherPriority.Loaded);
    }
    public void Rebuild()
    {
        if (_vm is null) return;
        var search = BranchSearch.Text?.Trim() ?? "";
        bool Matches(string value) => value.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        bool ActionMatches(string key) => Matches(LocalizedText.Get(key));
        UpdateAction.IsVisible = ActionMatches("git.workspace.update");
        CommitAction.IsVisible = ActionMatches("git.workspace.commit_action");
        PushAction.IsVisible = ActionMatches("git.workspace.push");
        NewBranchAction.IsVisible = ActionMatches("git.workspace.new_branch");
        CheckoutAction.IsVisible = ActionMatches("git.workspace.checkout_revision");
        UpdateAction.IsEnabled = CommitAction.IsEnabled = PushAction.IsEnabled = NewBranchAction.IsEnabled = CheckoutAction.IsEnabled = _vm.CanManage;
        Nodes.Clear();
        var repoId = _vm.SelectedRepository?.Id ?? "";
        var branches = _vm.Branches.Where(branch => Matches(branch.Name) || Matches(branch.Tracking ?? "")).ToArray();
        GitHeaderBranchNode Group(string key) { var node = new GitHeaderBranchNode(LocalizedText.Get(key)); Nodes.Add(node); return node; }
        if (_recent.TryGetValue(repoId, out var recent))
        {
            var group = Group("git.workspace.recent");
            foreach (var name in recent)
                if (branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == name) is { } branch) AddPath(group, branch, branch.Name);
            if (group.Children.Count == 0) Nodes.Remove(group);
        }
        foreach (var remote in new[] { false, true })
        {
            var group = Group(remote ? "git.workspace.remote" : "git.workspace.local");
            foreach (var branch in branches.Where(branch => branch.IsRemote == remote).OrderByDescending(IsFavorite).ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                AddPath(group, branch, branch.Name);
            if (group.Children.Count == 0 && search.Length > 0) Nodes.Remove(group);
        }
        var tags = Group("git.workspace.tags");
        foreach (var tag in _vm.Tags.Where(tag => Matches(tag.Name)))
            tags.Children.Add(new(tag.Name, new("refs/tags/" + tag.Name, tag.Sha), true));
        if (tags.Children.Count == 0 && search.Length > 0) Nodes.Remove(tags);
    }
    private bool IsFavorite(GitBranchDto branch) => _vm?.SelectedRepository is { } repo
        && _favorites.TryGetValue(repo.Id, out var names) && names.Contains((branch.IsRemote ? "remote:" : "local:") + branch.Name);
    private void AddPath(GitHeaderBranchNode parent, GitBranchDto branch, string path)
    {
        var parts = path.Split('/');
        foreach (var part in parts[..^1])
        {
            var folder = parent.Children.FirstOrDefault(node => node.IsGroup && node.Name == part);
            if (folder is null) { folder = new(part); parent.Children.Add(folder); }
            parent = folder;
        }
        parent.Children.Add(new(parts[^1], branch) { Icon = IsFavorite(branch) ? "Star" : branch.IsCurrent ? "Tag" : "Branch" });
    }
    private void Search_Changed(object? sender, TextChangedEventArgs e) => Rebuild();
    private void Update_Click(object? sender, RoutedEventArgs e) { if (_vm?.CanManage != true) return; _close?.Invoke(); _vm.PullCommand.Execute(null); }
    private void Push_Click(object? sender, RoutedEventArgs e) { if (_vm?.CanManage != true) return; _close?.Invoke(); _vm.PushCommand.Execute(null); }
    private void Commit_Click(object? sender, RoutedEventArgs e) { _close?.Invoke(); _commit?.Invoke(); }
    private void NewBranch_Click(object? sender, RoutedEventArgs e) { if (_vm?.CanManage != true) return; _close?.Invoke(); _vm.CreateBranchCommand.Execute(null); }
    private async void CheckoutRevision_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.CanManage != true) return;
        _close?.Invoke();
        if (_vm is not null) await _vm.CheckoutReferenceCommand.ExecuteAsync(null);
    }
    private void Options_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || _vm is null) return;
        var menu = new MenuFlyout();
        var fetch = new MenuItem { Header = LocalizedText.Get("git.log.menu.fetch_all"), Command = _vm.FetchCommand };
        fetch.Click += (_, _) => _close?.Invoke();
        menu.Items.Add(fetch);
        var remotes = new MenuItem { Header = LocalizedText.Get("git.page.remotes") };
        remotes.Click += (_, _) => { _close?.Invoke(); _openTool?.Invoke("remotes"); };
        menu.Items.Add(remotes);
        var branches = new MenuItem { Header = LocalizedText.Get("git.editor.branches") };
        branches.Click += (_, _) => { _close?.Invoke(); _openTool?.Invoke("branches"); };
        menu.Items.Add(branches); menu.ShowAt(anchor);
    }
    public MenuFlyout BuildBranchMenu(GitHeaderBranchNode node)
    {
        var menu = new MenuFlyout();
        if (_vm is null || node.Branch is not { } branch) return menu;
        _vm.SelectedBranch = branch;
        void Add(string key, ICommand command, bool enabled = true)
        {
            var item = new MenuItem { Header = LocalizedText.Get(key), Command = command, CommandParameter = branch, IsEnabled = enabled && _vm.CanManage };
            item.Click += (_, _) => _close?.Invoke();
            menu.Items.Add(item);
        }
        Add("git.log.menu.checkout", _vm.CheckoutBranchCommand, !branch.IsCurrent);
        if (!node.IsTag) Add("git.log.menu.checkout_and_update", _vm.CheckoutAndUpdateBranchCommand, !branch.IsRemote);
        Add("git.log.menu.create_branch_here", _vm.CreateBranchFromHereCommand);
        if (!node.IsTag) Add("git.log.menu.rename", _vm.RenameBranchCommand, !branch.IsRemote);
        Add("git.workspace.merge", _vm.MergeBranchCommand, !branch.IsCurrent);
        Add("git.workspace.rebase", _vm.RebaseBranchCommand, !branch.IsCurrent);
        var compare = new MenuItem { Header = LocalizedText.Get("git.log.menu.diff_working_tree"), IsEnabled = _vm.CanManage };
        compare.Click += async (_, _) => { _close?.Invoke(); _openTool?.Invoke("log"); await _vm.CompareBranchCommand.ExecuteAsync(branch); };
        menu.Items.Add(compare);
        if (!node.IsTag)
        {
            menu.Items.Add(new Separator());
            Add("git.log.menu.push", _vm.PushBranchCommand, !branch.IsRemote);
            Add("git.log.menu.tracked_branch", _vm.SetUpstreamBranchCommand, !branch.IsRemote);
            Add("git.log.menu.delete_branch", _vm.DeleteBranchContextCommand, !branch.IsCurrent && !branch.IsRemote);
            var favorite = new MenuItem { Header = LocalizedText.Get(IsFavorite(branch) ? "git.workspace.unfavorite" : "git.workspace.favorite") };
            favorite.Click += (_, _) =>
            {
                if (_vm.SelectedRepository is not { } repo) return;
                if (!_favorites.TryGetValue(repo.Id, out var names)) _favorites[repo.Id] = names = [];
                var key = (branch.IsRemote ? "remote:" : "local:") + branch.Name;
                if (!names.Add(key)) names.Remove(key);
                Rebuild();
            };
            menu.Items.Add(favorite);
        }
        return menu;
    }
    private void Branch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitHeaderBranchNode node } anchor) BuildBranchMenu(node).ShowAt(anchor);
    }
    private async void Branches_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || HeaderBranchList.SelectedItem is not GitHeaderBranchNode { Branch: { } branch } || _vm is null || !_vm.CanManage) return;
        e.Handled = true; _close?.Invoke();
        await _vm.CheckoutBranchCommand.ExecuteAsync(branch);
    }
}
