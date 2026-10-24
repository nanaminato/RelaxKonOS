using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>三栏统一日志视图：左分支树 + 中提交列表 + 右文件变更+详情。
/// 在 Loaded 时为 DataContext（GitClientViewModel）的 Branches 构造树形视图模型，同时响应搜索文本过滤。
/// 右侧面板复用 VM 中 SelectedCommit → OnSelectedCommitChanged 加载 CommitDetail/CommitChangedFiles。</summary>
internal partial class GitLogView : UserControl
{
    public ObservableCollection<BranchTreeNode> BranchTreeRoots { get; } = [];
    public ObservableCollection<CommitFileTreeNode> CommitFileTreeRoots { get; } = [];

    private GitClientViewModel? _vm;
    public static readonly StyledProperty<double> GraphWidthProperty = AvaloniaProperty.Register<GitLogView, double>(nameof(GraphWidth), 22);
    public double GraphWidth { get => GetValue(GraphWidthProperty); set => SetValue(GraphWidthProperty, value); }
    public static readonly StyledProperty<bool> ShowAuthorsProperty = AvaloniaProperty.Register<GitLogView, bool>(nameof(ShowAuthors), true);
    public bool ShowAuthors { get => GetValue(ShowAuthorsProperty); set => SetValue(ShowAuthorsProperty, value); }
    public static readonly StyledProperty<bool> ShowDatesProperty = AvaloniaProperty.Register<GitLogView, bool>(nameof(ShowDates), true);
    public bool ShowDates { get => GetValue(ShowDatesProperty); set => SetValue(ShowDatesProperty, value); }
    public static readonly StyledProperty<bool> ShowReferencesProperty = AvaloniaProperty.Register<GitLogView, bool>(nameof(ShowReferences), true);
    public bool ShowReferences { get => GetValue(ShowReferencesProperty); set => SetValue(ShowReferencesProperty, value); }
    public ObservableCollection<GitLogRow> LogRows { get; } = [];
    private bool _rowsPending, _rebuildingRows, _initialized;
    private bool? _showBranches;
    private bool _singleClickFilters = true;
    private double _branchWidth = 300, _filesWidth = 280;
    private readonly HashSet<string> _favorites = [];
    private bool _commitTreeRebuildPending;
    private bool _attached;
    private string? _filesFingerprint, _selectedFilePath;
    private bool _rebuildingFiles, _rebuildingBranches;
    private string? _selectedBranchName;
    private readonly Dictionary<string, bool> _branchExpansionState = new(StringComparer.Ordinal);

    public GitLogView()
    {
        InitializeComponent();
        _initialized = true;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var compact = !(_showBranches ?? Bounds.Width >= 800);
        BranchPane.IsVisible = BranchSplitter.IsVisible = !compact;
        ShowBranchesButton.IsVisible = compact;
        LogLayoutRoot.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : Math.Min(_branchWidth, Math.Max(160, Bounds.Width * .35)));
        LogLayoutRoot.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 4);
        LogLayoutRoot.ColumnDefinitions[4].Width = new GridLength(Math.Min(_filesWidth, Bounds.Width < 800 ? 180 : Math.Max(180, Bounds.Width * .27)));
        var center = Bounds.Width - LogLayoutRoot.ColumnDefinitions[0].Width.Value - LogLayoutRoot.ColumnDefinitions[4].Width.Value - 8;
        if (_vm?.IsInlineDiffOpen == true) center /= 2.5;
        ShowAuthors = AuthorsOption.IsChecked == true && center >= 400;
        ShowDates = DatesOption.IsChecked == true && center >= 550;
        ShowReferences = ReferencesOption.IsChecked == true && center >= 480;
    }
    public void SetBranchesVisible(bool visible) { if (!visible && LogLayoutRoot.ColumnDefinitions[0].Width.Value > 0) _branchWidth = LogLayoutRoot.ColumnDefinitions[0].Width.Value; _showBranches = visible; ApplyResponsiveLayout(); }
    public GitLogView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_attached) return;
        _attached = true;
        if (_vm is null && DataContext is GitClientViewModel vm) _vm = vm;
        if (_vm is null) return;

        // 把 ViewModel 的 Branches 扁平列表映射为「HEAD / 本地 / 远程 / 每个 remote」 树形结构
        RebuildBranchTree();

        _vm.Branches.CollectionChanged += BranchesChanged;
        _vm.Tags.CollectionChanged += BranchesChanged;
        _vm.Commits.CollectionChanged += CommitsChanged;
        _vm.GraphCommits.CollectionChanged += CommitsChanged;
        _vm.CommitChangedFiles.CollectionChanged += FilesChanged;
        CommitList.ItemsSource = LogRows;
        RebuildLogRows();
        // 搜索框变化时重建（过滤）分支树；Status 更新时 HEAD 文案可能变
        _vm.PropertyChanged += ViewModel_PropertyChanged;

        // 将 TreeView 绑定到本地树形集合（XAML 默认扁平，这里替换为分组树）
        BranchTree.ItemsSource = BranchTreeRoots;
        ChangedFileTree.ItemsSource = CommitFileTreeRoots;
        RebuildCommitFileTree();
        ApplyDiffPaneLayout();
        UpdateActionButtons();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _attached = false;
        if (LogLayoutRoot.ColumnDefinitions[0].Width.Value > 0) _branchWidth = LogLayoutRoot.ColumnDefinitions[0].Width.Value;
        if (LogLayoutRoot.ColumnDefinitions[4].Width.Value > 0) _filesWidth = LogLayoutRoot.ColumnDefinitions[4].Width.Value;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= ViewModel_PropertyChanged;
            _vm.Branches.CollectionChanged -= BranchesChanged; _vm.Tags.CollectionChanged -= BranchesChanged;
            _vm.Commits.CollectionChanged -= CommitsChanged; _vm.GraphCommits.CollectionChanged -= CommitsChanged;
            _vm.CommitChangedFiles.CollectionChanged -= FilesChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GitClientViewModel.BranchSearchText)
            or nameof(GitClientViewModel.Status))
            RebuildBranchTree();
        if (e.PropertyName is nameof(GitClientViewModel.IsBusy) or nameof(GitClientViewModel.SelectedBranch) or nameof(GitClientViewModel.SelectedCommit)) UpdateActionButtons();
        if (e.PropertyName == nameof(GitClientViewModel.SelectedLogBranch)) { _selectedBranchName = _vm?.SelectedLogBranch?.Value; RestoreBranchSelection(); }
        if (e.PropertyName == nameof(GitClientViewModel.SelectedCommit))
            CommitList.SelectedItem = LogRows.FirstOrDefault(row => row.Commit.Sha == _vm?.SelectedCommit?.Sha);
        if (e.PropertyName is nameof(GitClientViewModel.IsInlineDiffOpen))
            ApplyDiffPaneLayout();
        if (e.PropertyName is nameof(GitClientViewModel.Status) or nameof(GitClientViewModel.CommitChangedFiles))
            QueueCommitFileTreeRebuild();
    }

    /// <summary>
    /// The diff pane is a real column, not an overlay: the commit list and the file tree give up
    /// width so the patch gets it. Collapsing the column back to zero restores the three-pane
    /// layout exactly, including a splitter the user dragged.
    /// </summary>
    private void ApplyDiffPaneLayout()
    {
        var open = _vm?.IsInlineDiffOpen == true;
        LogLayoutRoot.ColumnDefinitions[5].Width = new GridLength(open ? 5 : 0);
        LogLayoutRoot.ColumnDefinitions[6].Width = open
            ? new GridLength(1.5, GridUnitType.Star)
            : new GridLength(0);
        ApplyResponsiveLayout();
    }

    private void DiffPane_CloseRequested(object? sender, RoutedEventArgs e)
        => _vm?.CloseInlineDiffCommand.Execute(null);

    private void BranchTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _rebuildingBranches) return;
        var branch = (BranchTree.SelectedItem as BranchTreeNode)?.Branch;
        _selectedBranchName = branch?.Name;
        _vm.SelectedBranch = branch;
        if (_singleClickFilters && branch is not null) FilterBranch(branch);
    }

    private void BranchNode_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { DataContext: BranchTreeNode { Branch: { } branch } }) return;
        FilterBranch(branch); e.Handled = true;
    }
    private void FilterBranch(GitBranchDto branch)
    {
        var option = _vm?.LogBranchOptions.FirstOrDefault(option => option.Value == branch.Name);
        if (option is not null) _vm!.SelectedLogBranch = option;
    }

    private void RebuildBranchTree()
    {
        if (_vm is null) return;
        CaptureBranchExpansionState();
        var filter = (_vm.BranchSearchText ?? string.Empty).Trim();
        bool Pass(string s) => string.IsNullOrEmpty(filter)
            || s.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var all = _vm.Branches.ToList();
        var currentBranchName = _vm.Status?.Branch;

        _rebuildingBranches = true;
        try
        {
        BranchTreeRoots.Clear();

        // 1. HEAD 伪节点
        var head = all.FirstOrDefault(b => b.IsCurrent);
        if (head is not null)
        {
            BranchTreeRoots.Add(new BranchTreeNode(
                displayName: string.IsNullOrEmpty(currentBranchName)
                    ? LocalizedText.Format("git.log.head_format", head.Name)
                    : LocalizedText.Format("git.log.head_current_branch_format", head.Name),
                icon: "⭐",
                foreground: ThemeBrushes.Get("TextPrimaryBrush"),
                fontWeight: FontWeight.SemiBold,
                badge: string.Empty,
                branch: head,
                nodeKind: BranchNodeKind.Head));
        }
        else if (!string.IsNullOrEmpty(currentBranchName))
        {
            BranchTreeRoots.Add(new BranchTreeNode(
                displayName: LocalizedText.Format("git.log.head_current_branch_format", currentBranchName),
                icon: "⭐",
                foreground: ThemeBrushes.Get("TextPrimaryBrush"),
                fontWeight: FontWeight.SemiBold,
                nodeKind: BranchNodeKind.Head));
        }

        // 2. 本地分支分组
        var locals = all.Where(b => !b.IsRemote).ToList();
        var localGroup = new BranchTreeNode(
            LocalizedText.Get("git.log.branch_tree_local"),
            icon: "📁",
            nodeKind: BranchNodeKind.Group,
            isExpanded: GetBranchExpansionState("local", defaultValue: true));
        localGroup.Key = "local";
        foreach (var b in locals)
        {
            if (!Pass(b.Name)) continue;
            var fg = b.IsCurrent ? ThemeBrushes.Get("SuccessBrush") : ThemeBrushes.Get("TextPrimaryBrush");
            var weight = b.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal;
            var icon = b.IsCurrent ? "⭐" : "🌱";
            var badge = b.Ahead > 0 || b.Behind > 0
                ? $"{(b.Ahead > 0 ? $"↑{b.Ahead}" : "")}{(b.Behind > 0 ? $"↓{b.Behind}" : "")}"
                : string.Empty;
            AddBranchPath(localGroup, b.Name, new BranchTreeNode(
                displayName: b.Name.Split('/').Last(),
                icon: icon,
                foreground: fg,
                fontWeight: weight,
                badge: badge,
                branch: b,
                nodeKind: BranchNodeKind.LocalBranch,
                canDelete: !b.IsCurrent,
                isBranchAndNotCurrent: !b.IsCurrent,
                currentBranchName: currentBranchName));
        }
        BranchTreeRoots.Add(localGroup);

        // 3. 远程分组：按 remote 名（remote/name 中 slash 前）分组
        var remotes = all.Where(b => b.IsRemote).ToList();
        var remoteRoot = new BranchTreeNode(
            LocalizedText.Get("git.log.branch_tree_remote"),
            icon: "🌐",
            nodeKind: BranchNodeKind.Group,
            isExpanded: GetBranchExpansionState("remotes", defaultValue: true));
        remoteRoot.Key = "remotes";
        var remoteGroups = new Dictionary<string, BranchTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in remotes)
        {
            var slash = b.Name.IndexOf('/');
            var remoteName = slash > 0 ? b.Name[..slash] : "origin";
            var shortName = slash > 0 ? b.Name[(slash + 1)..] : b.Name;
            if (!Pass(remoteName) && !Pass(b.Name) && !Pass(shortName)) continue;

            if (!remoteGroups.TryGetValue(remoteName, out var rg))
            {
                rg = new BranchTreeNode(
                    remoteName,
                    icon: "📂",
                    nodeKind: BranchNodeKind.RemoteGroup,
                    isExpanded: GetBranchExpansionState($"remote:{remoteName}", defaultValue: true));
                rg.Key = "remote:" + remoteName;
                remoteGroups[remoteName] = rg;
                remoteRoot.Children.Add(rg);
            }
            AddBranchPath(rg, shortName, new BranchTreeNode(
                displayName: shortName.Split('/').Last(),
                icon: "🔗",
                foreground: ThemeBrushes.Get("TextSecondaryBrush"),
                fontWeight: FontWeight.Normal,
                badge: string.Empty,
                branch: b,
                nodeKind: BranchNodeKind.RemoteBranch,
                canDelete: false,
                currentBranchName: currentBranchName));
        }
        BranchTreeRoots.Add(remoteRoot);
        var tags = new BranchTreeNode(LocalizedText.Get("git.workspace.tags"), "📁", nodeKind: BranchNodeKind.Group, isExpanded: GetBranchExpansionState("tags", true)) { Key = "tags" };
        foreach (var tag in _vm.Tags.Where(tag => Pass(tag.Name)))
            AddBranchPath(tags, tag.Name, new BranchTreeNode(tag.Name.Split('/').Last(), "tag", branch: new GitBranchDto("refs/tags/" + tag.Name, tag.Sha), nodeKind: BranchNodeKind.Tag));
        BranchTreeRoots.Add(tags);
        RestoreBranchSelection();
        }
        finally { _rebuildingBranches = false; }
    }

    private void AddBranchPath(BranchTreeNode parent, string path, BranchTreeNode node)
    {
        var key = parent.Key ?? parent.DisplayName;
        foreach (var part in path.Split('/')[..^1])
        {
            key += "/" + part;
            var group = parent.Children.FirstOrDefault(child => child.Key == key);
            if (group is null) { group = new BranchTreeNode(part, "📁", nodeKind: BranchNodeKind.Group, isExpanded: GetBranchExpansionState(key, true)) { Key = key }; parent.Children.Add(group); }
            parent = group;
        }
        node.IsFavorite = node.Branch is not null && _favorites.Contains(node.Branch.Name);
        parent.Children.Add(node);
    }
    private void CaptureBranchExpansionState()
    {
        void Capture(BranchTreeNode node)
        {
            if (node.Key is not null) _branchExpansionState[node.Key] = node.IsExpanded;
            foreach (var child in node.Children) Capture(child);
        }
        foreach (var root in BranchTreeRoots) Capture(root);
        var local = BranchTreeRoots.FirstOrDefault(node => node.Kind == BranchNodeKind.Group && node.Icon == "📁" && node.Key != "tags");
        if (local is not null) _branchExpansionState["local"] = local.IsExpanded;
        var remotes = BranchTreeRoots.FirstOrDefault(node => node.Icon == "🌐");
        if (remotes is not null) { _branchExpansionState["remotes"] = remotes.IsExpanded; foreach (var remote in remotes.Children) _branchExpansionState[$"remote:{remote.DisplayName}"] = remote.IsExpanded; }
    }

    private bool GetBranchExpansionState(string key, bool defaultValue)
        => _branchExpansionState.TryGetValue(key, out var isExpanded) ? isExpanded : defaultValue;

    private void QueueCommitFileTreeRebuild()
    {
        if (_commitTreeRebuildPending) return;
        _commitTreeRebuildPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _commitTreeRebuildPending = false;
            if (_attached) RebuildCommitFileTree();
        });
    }

    private void RebuildCommitFileTree()
    {
        if (_vm is null) return;
        UpdateCommitFileCount();

        var fingerprint = _vm.SelectedCommit?.Sha + "|" + LocalizedText.Get("git.log.file_count_format") + "|" + string.Join(";", _vm.CommitChangedFiles.Select(file => file.Path + ":" + file.Status));
        if (fingerprint == _filesFingerprint) return;
        _filesFingerprint = fingerprint;
        _rebuildingFiles = true;
        _vm.SelectedCommitFile = null;
        CommitFileTreeRoots.Clear();
        var roots = new ObservableCollection<CommitFileTreeNode>();
        var directories = new Dictionary<string, CommitFileTreeNode>(StringComparer.Ordinal);

        foreach (var file in _vm.CommitChangedFiles.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = file.Path
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) continue;

            var parent = roots;
            var directoryPath = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                var isFile = index == segments.Length - 1;
                if (isFile)
                {
                    parent.Add(new CommitFileTreeNode(segments[index], file));
                    continue;
                }

                directoryPath = string.IsNullOrEmpty(directoryPath)
                    ? segments[index]
                    : $"{directoryPath}/{segments[index]}";
                if (!directories.TryGetValue(directoryPath, out var directory))
                {
                    directory = new CommitFileTreeNode(segments[index]);
                    directories[directoryPath] = directory;
                    parent.Add(directory);
                }
                parent = directory.Children;
            }
        }
        void Compact(CommitFileTreeNode node)
        {
            while (!node.IsFile && node.Children.Count == 1 && !node.Children[0].IsFile)
            {
                var child = node.Children[0]; node.Name += "/" + child.Name;
                var children = child.Children.ToArray(); node.Children.Clear(); foreach (var item in children) node.Children.Add(item);
            }
            foreach (var child in node.Children) Compact(child);
        }
        foreach (var root in roots) { Compact(root); CommitFileTreeRoots.Add(root); }
        CommitFileTreeNode? Find(IEnumerable<CommitFileTreeNode> nodes) => nodes.SelectMany(node => node.IsFile ? new[] { node } : Flatten(node.Children)).FirstOrDefault(node => node.File?.Path == _selectedFilePath);
        IEnumerable<CommitFileTreeNode> Flatten(IEnumerable<CommitFileTreeNode> nodes) => nodes.SelectMany(node => node.IsFile ? new[] { node } : Flatten(node.Children));
        ChangedFileTree.SelectedItem = Find(CommitFileTreeRoots);
        _rebuildingFiles = false;
    }

    private void UpdateCommitFileCount()
    {
        if (_vm is not null) CommitFileCount.Text = LocalizedText.Format("git.log.file_count_format", _vm.CommitChangedFiles.Count);
    }

    private void ChangedFileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedCommitFile = (ChangedFileTree.SelectedItem as CommitFileTreeNode)?.File;
        if (!_rebuildingFiles) _selectedFilePath = _vm.SelectedCommitFile?.Path;
        if (DiffPreviewToggle.IsChecked == true && _vm.SelectedCommitFile is { } file) _ = _vm.PreviewLogFileDiffAsync(file);
    }
}

/// <summary>分支/分组树节点（用于 TreeView）。</summary>
public sealed class BranchTreeNode : ObservableObject
{
    public string DisplayName { get; }
    public string Icon { get; }
    public IBrush Foreground { get; }
    public FontWeight FontWeight { get; }
    public string Badge { get; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public GitBranchDto? Branch { get; }
    public BranchNodeKind Kind { get; }
    public string IconKind => IsFavorite ? "Star" : Kind == BranchNodeKind.Tag || Branch?.IsCurrent == true ? "Tag" : Kind switch { BranchNodeKind.Group => "Folder", BranchNodeKind.RemoteGroup => "Remote", BranchNodeKind.Head => "Commit", _ => "Branch" };
    public bool CanDelete { get; }
    public bool IsBranchAndNotCurrent { get; }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string? Key { get; set; }
    public bool IsFavorite { get; set; }

    public bool IsBranch => Kind is BranchNodeKind.LocalBranch or BranchNodeKind.RemoteBranch or BranchNodeKind.Head;
    public bool IsLocalBranch => Kind == BranchNodeKind.LocalBranch;
    public bool IsRemoteBranch => Kind == BranchNodeKind.RemoteBranch;
    public bool IsActionableBranch => Kind is BranchNodeKind.LocalBranch or BranchNodeKind.RemoteBranch;
    public bool IsCurrentLocalBranch => IsLocalBranch && Branch?.IsCurrent == true;
    public bool CanCheckout => IsActionableBranch && !IsCurrentLocalBranch;
    public bool CanCheckoutAndUpdate => IsLocalBranch && !IsCurrentLocalBranch;
    public bool CanMergeIntoCurrent => IsActionableBranch && !IsCurrentLocalBranch;
    // Git can push or fast-forward a local ref without checking it out. This lets
    // the context menu operate on e.g. branch b while HEAD remains on branch a.
    public bool CanPush => IsLocalBranch;
    public bool CanUpdate => IsLocalBranch || IsRemoteBranch;
    public bool CanRename => IsLocalBranch;
    public string NewBranchMenuText => IsActionableBranch
        ? LocalizedText.Format("git.log.menu.create_branch_from_format", Branch!.Name)
        : LocalizedText.Get("git.log.menu.new_branch");
    public string CompareMenuText => LocalizedText.Format("git.log.menu.compare_with_current_format",
        string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName);
    public string MergeMenuText => LocalizedText.Format("git.log.menu.merge_into_current_format", Branch?.Name ?? DisplayName,
        string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName);
    public string PullMenuText => IsRemoteBranch
        ? LocalizedText.Format("git.log.menu.pull_remote_into_current_format", Branch?.Name ?? DisplayName,
            string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName)
        : LocalizedText.Get("git.log.menu.update");
    public string? CurrentBranchName { get; }

    public ObservableCollection<BranchTreeNode> Children { get; } = [];

    public BranchTreeNode(string displayName, string icon = "📄",
        IBrush? foreground = null, FontWeight fontWeight = default, string badge = "",
        GitBranchDto? branch = null, BranchNodeKind nodeKind = BranchNodeKind.Item,
        bool canDelete = false, bool isBranchAndNotCurrent = false, bool isExpanded = false,
        string? currentBranchName = null)
    {
        DisplayName = displayName;
        Icon = icon;
        Foreground = foreground ?? ThemeBrushes.Get("TextPrimaryBrush");
        FontWeight = fontWeight == default ? FontWeight.Normal : fontWeight;
        Badge = badge;
        Branch = branch;
        Kind = nodeKind;
        CanDelete = canDelete;
        IsBranchAndNotCurrent = isBranchAndNotCurrent;
        IsExpanded = isExpanded;
        CurrentBranchName = currentBranchName;
    }
}

public enum BranchNodeKind { Item, Group, Head, LocalBranch, RemoteBranch, RemoteGroup, Tag }

/// <summary>A node in the selected commit's changed-file tree. Directory nodes have no associated file.</summary>
public sealed class CommitFileTreeNode : ObservableObject
{
    public CommitFileTreeNode(string name, GitFileChangeDto? file = null)
    {
        Name = name;
        File = file;
    }

    public string Name { get; set; }
    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public int FileCount => IsFile ? 1 : Children.Sum(child => child.FileCount);
    public string CountLabel => LocalizedText.Format("git.log.file_count_format", FileCount);
    public GitFileChangeDto? File { get; }
    public bool IsFile => File is not null;
    public RelaxKonOS.Protocol.Files.FileSystemEntryType EntryType => IsFile ? RelaxKonOS.Protocol.Files.FileSystemEntryType.File : RelaxKonOS.Protocol.Files.FileSystemEntryType.Directory;
    public string Icon => IsFile ? "📄" : "📁";
    public string Status => File?.Status ?? string.Empty;
    public ObservableCollection<CommitFileTreeNode> Children { get; } = [];
}

/// <summary>GitLogView XAML 中用到的一组值转换器（单例，x:Static 引用）。</summary>
public static class GitLogConverters
{
    public static readonly IValueConverter StatusBgConverter = new FileStatusBgConverter();
    public static readonly IValueConverter StatusLabelConverter = new FileStatusLabelConverter();
    public static readonly IValueConverter CommitDateConverter = new CommitDateConverter();
    public static readonly IValueConverter MergeOpacityConverter = new FuncValueConverter<bool, double>(merge => merge ? .6 : 1);
}

public sealed class FileStatusBgConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string) ?? string.Empty;
        return s.ToLowerInvariant() switch
        {
            "added" => ThemeBrushes.Get("SuccessBrush"),
            "modified" => ThemeBrushes.Get("InfoBrush"),
            "deleted" => ThemeBrushes.Get("DangerBrush"),
            "renamed" => ThemeBrushes.Get("AccentBrush"),
            "copied" => ThemeBrushes.Get("AccentBrush"),
            "untracked" => ThemeBrushes.Get("TextTertiaryBrush"),
            "conflicted" => ThemeBrushes.Get("DangerBrush"),
            _ => ThemeBrushes.Get("TextPrimaryBrush"),
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FileStatusLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string) ?? string.Empty;
        return s.ToLowerInvariant() switch
        {
            "added" => "A",
            "modified" => "M",
            "deleted" => "D",
            "renamed" => "R",
            "copied" => "C",
            "untracked" => "U",
            "conflicted" => "!",
            _ => "·",
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Compact ISO commit dates, retaining the original value if parsing fails.</summary>
public sealed class CommitDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return value;
        var today = DateTimeOffset.UtcNow.ToOffset(date.Offset).Date;
        if (date.Date == today) return LocalizedText.Format("git.dock.today_time", date.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (date.Date == today.AddDays(-1)) return LocalizedText.Format("git.dock.yesterday_time", date.ToString("HH:mm", CultureInfo.InvariantCulture));
        return date.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
