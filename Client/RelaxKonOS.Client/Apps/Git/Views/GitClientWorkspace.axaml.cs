using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>IDE-style tool windows: the editor stays mounted and the Git log spans the bottom.</summary>
internal partial class GitClientWorkspace : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ProjectEditorViewModel _project;
    private RelaxKonOS.WindowManager.ManagedWindow? _headerWindow;
    private bool _attached;
    private string _tool = "log";
    private double _gitHeight = 260;
    private double _sidebarWidth = 300;
    private bool _menuExpanded;
    private readonly List<CodeEditorDocument> _navigationHistory = [];
    private int _navigationIndex = -1;
    private bool _navigating;
    private readonly DispatcherTimer _menuTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private GitClientWorkspace(GitClientViewModel viewModel, ProjectEditorViewModel project)
    {
        _viewModel = viewModel;
        _project = project;
        InitializeComponent();
        ProjectSwitcher.Width = BranchBadge.Width = double.NaN;
        MainMenu.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _menuExpanded = false; _menuTimer.Stop(); UpdateLayoutState(); CompactMenuButton.Focus(); e.Handled = true;
        };
        _menuTimer.Tick += (_, _) =>
        {
            if (DetailedMenuScroller.IsPointerOver || CompactMenuButton.IsPointerOver
                || MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubMenuOpen)) return;
            _menuExpanded = false;
            _menuTimer.Stop();
            UpdateLayoutState();
        };
        DataContext = viewModel;
        InitializeGitDock();
        BranchPopup.Configure(viewModel, () => BranchBadge.Flyout?.Hide(), OpenTool, ShowCommitTool);
        ProjectPopup.Configure(viewModel, project, () => ProjectSwitcher.Flyout?.Hide());
        BranchBadge.Flyout!.Closed += (_, _) => BranchPopup.EndSession();
        BranchBadge.Flyout!.Opened += async (_, _) => await BranchPopup.RefreshAsync();
        ProjectSwitcher.Flyout!.Opened += (_, _) => ProjectPopup.Rebuild();
        FileMenu.DataContext = EditMenu.DataContext = EditorStatus.DataContext = project.Editor;
        RefreshButton.Command = project.RefreshCommand;
        EditorPane.DataContext = project.Editor;
        EditorPane.UseProjectLayout();
        ProjectToolHost.Content = EditorPane.ProjectToolContent;
        if (project.ProjectName is { } name) Header.Title = name;
        Loaded += (_, _) =>
        {
            if (_attached) return;
            _attached = true;
            _viewModel.PropertyChanged += GitChanged;
            _viewModel.Remotes.CollectionChanged += RemotesChanged;
            _project.PropertyChanged += ProjectChanged;
            _project.Editor.PropertyChanged += EditorChanged;
            if (_project.IsGitVisible) ShowTool();
            UpdateLayoutState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _menuTimer.Stop();
            _menuExpanded = false;
            _viewModel.PropertyChanged -= GitChanged;
            _viewModel.Remotes.CollectionChanged -= RemotesChanged;
            _project.PropertyChanged -= ProjectChanged;
            _project.Editor.PropertyChanged -= EditorChanged;
            _attached = false;
        };
        SizeChanged += (_, _) => UpdateLayoutState();
    }

    public static GitClientWorkspace Create(GitClientViewModel viewModel, ProjectEditorViewModel project) => new(viewModel, project);
    public CodeEditorView EditorView => EditorPane;

    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        _headerWindow = window;
        if (_viewModel.IsPickerMode || ReferenceEquals(window.View.HeaderCenter, HeaderMenuHost)) return;
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
        HeaderMenuHost.DataContext = DataContext;
        if (HeaderMenuHost.Parent is Panel parent) parent.Children.Remove(HeaderMenuHost);
        window.View.HeaderCenter = HeaderMenuHost;
    }

    private void GitChanged(object? sender, PropertyChangedEventArgs e)
    {
        SyncGitDock(e);
        if (e.PropertyName == nameof(GitClientViewModel.IsPickerMode) && _headerWindow is not null)
        {
            if (_viewModel.IsPickerMode) Header.Detach();
            else AttachWindowHeader(_headerWindow);
        }
        if (e.PropertyName == nameof(GitClientViewModel.ActivePage) && !_selectingGitTab)
        {
            if (_viewModel.ActivePage == GitClientPage.Workspace) ShowCommitTool();
            else if (_viewModel.ActivePage is GitClientPage.Log or GitClientPage.Remotes)
            {
                _tool = _viewModel.ActivePage == GitClientPage.Log ? "log" : "remotes";
                _project.IsGitVisible = true; ShowTool(ownerLog: _tool == "log");
            }
        }
        if (e.PropertyName is nameof(GitClientViewModel.Status) or nameof(GitClientViewModel.SelectedRepository))
        {
            BranchPopup.RecordCurrentBranch();
            ProjectPopup.RecordCurrentProject();
        }
        if (e.PropertyName == nameof(GitClientViewModel.StatusText)) StatusLine.Text = _viewModel.StatusText;
        UpdateLayoutState();
    }

    private void ProjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectEditorViewModel.ProjectPath))
        {
            _navigationHistory.Clear(); _navigationIndex = -1;
            ProjectPopup.RecordCurrentProject();
        }
        if (e.PropertyName == nameof(ProjectEditorViewModel.ProjectName))
            Header.Title = _project.ProjectName ?? LocalizedText.Get("git.title");
        if (e.PropertyName == nameof(ProjectEditorViewModel.IsGitVisible) && _project.IsGitVisible) ShowTool();
        UpdateLayoutState();
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveDocument))
        {
            if (_project.Editor.ActiveDocument is not null) _gitMaximized = false;
            if (!_navigating && _project.Editor.ActiveDocument is { } document)
            {
                if (_navigationIndex + 1 < _navigationHistory.Count) _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
                if (_navigationHistory.LastOrDefault() != document) _navigationHistory.Add(document);
                _navigationIndex = _navigationHistory.Count - 1;
            }
            UpdateLayoutState();
        }
        if (e.PropertyName == nameof(CodeEditorViewModel.SelectedFolderNode)) UpdateLayoutState();
        if (e.PropertyName == nameof(CodeEditorViewModel.IsSidebarVisible)) UpdateLayoutState();
        if (e.PropertyName == nameof(CodeEditorViewModel.StatusText)) StatusLine.Text = _project.Editor.StatusText;
    }

    private void UpdateLayoutState()
    {
        InitializeButton.IsVisible = _viewModel.SelectedRepository is null;
        GitHubMenu.IsEnabled = GitHostingLinks.Resolve(_viewModel.Remotes, "github", "repo") is not null;
        GitLabMenu.IsEnabled = GitHostingLinks.Resolve(_viewModel.Remotes, "gitlab", "repo") is not null;
        AzureMenu.IsEnabled = GitHostingLinks.Resolve(_viewModel.Remotes, "azure", "repo") is not null;
        CurrentFileMenu.IsEnabled = CurrentGitFile() is not null;
        RenameFileItem.IsEnabled = _project.Editor.SelectedFolderNode is { IsDirectory: false, IsPlaceholder: false } && _project.Editor.CanEditDocument;
        ProjectNameLabel.MaxWidth = Math.Clamp(Bounds.Width - 400, 45, 160);
        ProjectMonogram.IsVisible = Bounds.Width >= 480;
        ProjectNameLabel.SetValue(ToolTip.TipProperty, _project.ProjectPath);
        MainMenu.IsVisible = DetailedMenuScroller.IsVisible = _menuExpanded;
        DetailedMenuScroller.MaxWidth = Math.Max(80, Bounds.Width - 220);
        CompactMenuButton.IsVisible = !_menuExpanded;
        ProjectSwitcher.IsVisible = !_menuExpanded;
        CompactNavigation.IsVisible = !_menuExpanded && Bounds.Width >= 600;
        BackButton.IsEnabled = FindNavigationTarget(-1) >= 0;
        ForwardButton.IsEnabled = FindNavigationTarget(1) >= 0;
        BranchBadge.MaxWidth = Math.Clamp(Bounds.Width - 390, 100, 230);
        BranchBadge.IsVisible = !_menuExpanded && _viewModel.SelectedRepository is not null && Bounds.Width >= 480;
        EditorStatus.IsVisible = Bounds.Width >= 600;

        var commit = _project.IsCommitVisible && _viewModel.SelectedRepository is not null;
        var project = !commit && _project.Editor.IsSidebarVisible;
        var sidebar = commit || project;
        var overlay = Bounds.Width < 760;
        if (MainWorkspace.ColumnDefinitions[0].Width.Value > 0)
            _sidebarWidth = MainWorkspace.ColumnDefinitions[0].Width.Value;
        var available = Math.Max(0, Bounds.Width - 38);
        var width = Math.Min(_sidebarWidth, overlay ? Math.Max(0, available - 32) : Math.Max(180, available * .42));
        MainWorkspace.ColumnDefinitions[0].Width = new GridLength(sidebar && !overlay ? width : 0);
        MainWorkspace.ColumnDefinitions[1].Width = new GridLength(sidebar && !overlay ? 4 : 0);
        Grid.SetColumn(SidebarHost, overlay ? 2 : 0);
        SidebarHost.Width = overlay ? width : double.NaN;
        SidebarHost.HorizontalAlignment = overlay ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        SidebarHost.IsVisible = sidebar;
        CommitTool.IsVisible = commit;
        ProjectTool.IsVisible = project;
        CommitSplitter.IsVisible = sidebar && !overlay;
        ProjectToolButton.IsChecked = project;
        CommitToolButton.IsChecked = commit;

        var showGit = _project.IsGitVisible && _viewModel.SelectedRepository is not null;
        if (!_gitMaximized && WorkspaceBody.RowDefinitions[0].Height.IsStar && WorkspaceBody.RowDefinitions[2].Height.Value > 0)
            _gitHeight = WorkspaceBody.RowDefinitions[2].Height.Value;
        WorkspaceBody.RowDefinitions[1].Height = new GridLength(showGit ? 4 : 0);
        WorkspaceBody.RowDefinitions[0].Height = showGit && _gitMaximized ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        MainWorkspace.IsVisible = !(showGit && _gitMaximized);
        WorkspaceBody.RowDefinitions[2].Height = new GridLength(showGit ? (_gitMaximized ? Math.Max(100, WorkspaceBody.Bounds.Height - 4) : Math.Min(_gitHeight, Math.Max(100, Bounds.Height * .42))) : 0);
        GitToolHost.IsVisible = GitSplitter.IsVisible = showGit;
        GitToolButton.IsChecked = showGit;
        GitTabScroll.MaxWidth = Math.Max(50, Bounds.Width - 235);
        DuplicateGitLogItem.IsEnabled = SelectedGitTab?.Session is not null;
        UpdateGitTabTitles();
    }

    private void ShowTool(bool ownerLog = false)
    {
        if (_dockRepository != _viewModel.SelectedRepository?.Id) ResetGitDock();
        var tab = ownerLog && _tool == "log" ? GitTabs.FirstOrDefault(tab => tab.Kind == "log" && tab.Session == _viewModel) : SelectedGitTab?.Kind == _tool ? SelectedGitTab : GitTabs.FirstOrDefault(tab => tab.Kind == _tool);
        if (tab is null)
        {
            var kind = _tool is "history" ? "log" : _tool;
            Control view = kind switch
            {
                "branches" => new GitBranchesView(_viewModel),
                "worktrees" or "stashes" => new GitWorkspaceMaintenanceView(_viewModel, kind),
                "remotes" => new GitRemotesView(_viewModel),
                "overview" => new GitOverviewView(_viewModel),
                "console" => new GitConsoleView(),
                _ => new GitLogView(_viewModel),
            };
            tab = AddGitTab(kind, view, kind == "log" ? _viewModel : null);
        }
        SelectGitTab(tab);
    }

    public void ToggleProjectTool() => _project.ToggleProjectCommand.Execute(null);
    public void ToggleCommitTool()
    {
        if (_viewModel.SelectedRepository is not null) _project.ToggleCommitCommand.Execute(null);
    }
    public void ToggleGitTool()
    {
        if (_viewModel.SelectedRepository is null) return;
        _project.IsGitVisible = !_project.IsGitVisible;
    }
    public void ShowCommitTool()
    {
        if (_viewModel.SelectedRepository is null) return;
        _project.IsCommitVisible = true;
        Dispatcher.UIThread.Post(CommitTool.FocusMessage, DispatcherPriority.Loaded);
    }

    private void Project_Click(object? sender, RoutedEventArgs e) => ToggleProjectTool();
    private void ExpandMenu_Click(object? sender, RoutedEventArgs e)
    {
        _menuExpanded = true;
        UpdateLayoutState();
        MainMenu.Focus();
        _menuTimer.Start();
    }

    private void Commit_Click(object? sender, RoutedEventArgs e) => ToggleCommitTool();
    private void GitWindow_Click(object? sender, RoutedEventArgs e) => ToggleGitTool();
    private void ShowCommit_Click(object? sender, RoutedEventArgs e) => ShowCommitTool();
    private void HideGit_Click(object? sender, RoutedEventArgs e) => _project.IsGitVisible = false;
    private void HideTools_Click(object? sender, RoutedEventArgs e)
    {
        _project.IsCommitVisible = false;
        _project.Editor.IsSidebarVisible = false;
        _project.IsGitVisible = false;
    }
    private async void OpenProject_Click(object? sender, RoutedEventArgs e) => await _viewModel.OpenFolderCommand.ExecuteAsync(null);
    private async void CloseProject_Click(object? sender, RoutedEventArgs e) => await _project.CloseProjectCommand.ExecuteAsync(null);
    private async void NewProject_Click(object? sender, RoutedEventArgs e) => await _viewModel.NewProjectCommand.ExecuteAsync(null);
    private async void CloneProject_Click(object? sender, RoutedEventArgs e) => await _viewModel.CloneRepositoryCommand.ExecuteAsync(null);
    private void Find_Click(object? sender, RoutedEventArgs e) => EditorPane.FocusFind();
    private async void Initialize_Click(object? sender, RoutedEventArgs e) => await _project.InitializeGitCommand.ExecuteAsync(null);
    private async void RefreshProject_Click(object? sender, RoutedEventArgs e) => await _project.RefreshAsync();
    private async void Stage_Click(object? sender, RoutedEventArgs e)
    {
        if (_project.Editor.CurrentPath is not { } path || _project.ProjectPath is not { } root || !_viewModel.CanManage) return;
        var relative = path.Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + '/';
        if (!relative.StartsWith(prefix, _viewModel.RepositoryPathComparison)) return;
        await _viewModel.StageFileContextCommand.ExecuteAsync(new RelaxKonOS.Protocol.Git.GitFileChangeDto(relative[prefix.Length..], Status: "modified"));
    }
    private void GitTool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tool }) OpenTool(tool);
    }
    private void OpenTool(string tool)
    {
        if (_viewModel.SelectedRepository is null) return;
        _tool = tool;
        _project.IsGitVisible = true;
        ShowTool(ownerLog: tool == "log");
        UpdateLayoutState();
        if (tool is "stashes" or "worktrees") _ = _viewModel.RefreshWorkspaceReferencesAsync();
    }
    private void RemotesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateLayoutState();
    private async void Hosting_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string target } || _viewModel.RequestOpenHostingUriAsync is null) return;
        var parts = target.Split(':');
        if (parts.Length != 2 || GitHostingLinks.Resolve(_viewModel.Remotes, parts[0], parts[1]) is not { } uri) return;
        try { await _viewModel.RequestOpenHostingUriAsync(uri); }
        catch (Exception ex) { if (_viewModel.ShowMessageAsync is not null) await _viewModel.ShowMessageAsync(ex.Message); }
    }
    private void ChooseEncoding_Click(object? sender, RoutedEventArgs e) => _project.Editor.ChooseEncodingCommand.Execute(null);
    private void Settings_Click(object? sender, RoutedEventArgs e) => _project.Editor.OpenSettingsCommand.Execute(null);
    private async void RenameCurrentFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_project.Editor.SelectedFolderNode is { IsDirectory: false } node)
            await _project.Editor.PerformTreeActionAsync(node, EditorTreeAction.Rename);
    }
    private async void Shortcuts_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.ShowMessageAsync is not null) await _viewModel.ShowMessageAsync(LocalizedText.Get("git.workspace.shortcuts_text"));
    }
    private int FindNavigationTarget(int direction)
    {
        for (var index = _navigationIndex + direction; index >= 0 && index < _navigationHistory.Count; index += direction)
            if (_project.Editor.OpenDocuments.Contains(_navigationHistory[index])) return index;
        return -1;
    }
    public void NavigateHistory(int direction)
    {
        var index = FindNavigationTarget(direction);
        if (index < 0) return;
        _navigating = true;
        try { _navigationIndex = index; _project.Editor.ActiveDocument = _navigationHistory[index]; }
        finally { _navigating = false; UpdateLayoutState(); }
    }
    private void Back_Click(object? sender, RoutedEventArgs e) => NavigateHistory(-1);
    private void Forward_Click(object? sender, RoutedEventArgs e) => NavigateHistory(1);
    private void NextTab_Click(object? sender, RoutedEventArgs e) => EditorPane.CycleDocument(1);
    private void PreviousTab_Click(object? sender, RoutedEventArgs e) => EditorPane.CycleDocument(-1);
    private void ShowBranchesPopup_Click(object? sender, RoutedEventArgs e)
    {
        _menuExpanded = false; _menuTimer.Stop(); UpdateLayoutState();
        Dispatcher.UIThread.Post(() => BranchBadge.Flyout?.ShowAt(BranchBadge.IsVisible ? BranchBadge : CompactMenuButton), DispatcherPriority.Loaded);
    }
    private void ShowProjectsPopup_Click(object? sender, RoutedEventArgs e)
    {
        _menuExpanded = false; _menuTimer.Stop(); UpdateLayoutState();
        Dispatcher.UIThread.Post(() => ProjectSwitcher.Flyout?.ShowAt(ProjectSwitcher), DispatcherPriority.Loaded);
    }
    private RelaxKonOS.Protocol.Git.GitFileChangeDto? CurrentGitFile()
    {
        if (_project.Editor.CurrentPath is not { } path || _project.ProjectPath is not { } root || !_viewModel.CanManage) return null;
        var relative = path.Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + '/';
        return relative.StartsWith(prefix, _viewModel.RepositoryPathComparison)
            ? new(relative[prefix.Length..]) : null;
    }
    private async void CurrentFile_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentGitFile() is not { } file || sender is not Control { Tag: string action }) return;
        switch (action)
        {
            case "diff": await _viewModel.ShowFileDiffContextCommand.ExecuteAsync(file); break;
            case "history": OpenTool("log"); await _viewModel.ShowFileHistoryCommand.ExecuteAsync(file); break;
            case "unstage": await _viewModel.UnstageFileCommand.ExecuteAsync(file); break;
            case "restore": await _viewModel.RevertFileChangeCommand.ExecuteAsync(file); break;
        }
    }
}
