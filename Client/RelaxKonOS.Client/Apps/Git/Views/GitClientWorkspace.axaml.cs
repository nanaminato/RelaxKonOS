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
    private readonly Dictionary<string, Control> _tools = [];

    private GitClientWorkspace(GitClientViewModel viewModel, ProjectEditorViewModel project)
    {
        _viewModel = viewModel;
        _project = project;
        InitializeComponent();
        DataContext = viewModel;
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
            _project.PropertyChanged += ProjectChanged;
            _project.Editor.PropertyChanged += EditorChanged;
            if (_project.IsGitVisible) ShowTool();
            UpdateLayoutState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged -= GitChanged;
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
        if (e.PropertyName == nameof(GitClientViewModel.IsPickerMode) && _headerWindow is not null)
        {
            if (_viewModel.IsPickerMode) Header.Detach();
            else AttachWindowHeader(_headerWindow);
        }
        if (e.PropertyName == nameof(GitClientViewModel.ActivePage))
        {
            _tool = _viewModel.ActivePage switch
            {
                GitClientPage.Log => "log", GitClientPage.Remotes => "remotes", _ => "overview",
            };
            _project.IsGitVisible = true;
            ShowTool();
        }
        if (e.PropertyName == nameof(GitClientViewModel.StatusText)) StatusLine.Text = _viewModel.StatusText;
        UpdateLayoutState();
    }

    private void ProjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectEditorViewModel.ProjectName))
            Header.Title = _project.ProjectName ?? LocalizedText.Get("git.title");
        if (e.PropertyName == nameof(ProjectEditorViewModel.IsGitVisible) && _project.IsGitVisible) ShowTool();
        UpdateLayoutState();
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.IsSidebarVisible)) UpdateLayoutState();
        if (e.PropertyName == nameof(CodeEditorViewModel.StatusText)) StatusLine.Text = _project.Editor.StatusText;
    }

    private void UpdateLayoutState()
    {
        InitializeButton.IsVisible = _viewModel.SelectedRepository is null;
        ProjectNameLabel.MaxWidth = Math.Clamp(Bounds.Width - 230, 50, 160);
        ProjectNameLabel.SetValue(ToolTip.TipProperty, _project.ProjectPath);
        var compact = Bounds.Width < 900;
        MainMenu.IsVisible = !compact;
        CompactMenuButton.IsVisible = compact;
        BranchBadge.IsVisible = _viewModel.SelectedRepository is not null && Bounds.Width >= 760;
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
        if (WorkspaceBody.RowDefinitions[2].Height.Value > 0)
            _gitHeight = WorkspaceBody.RowDefinitions[2].Height.Value;
        WorkspaceBody.RowDefinitions[1].Height = new GridLength(showGit ? 4 : 0);
        WorkspaceBody.RowDefinitions[2].Height = new GridLength(showGit ? Math.Min(_gitHeight, Math.Max(100, Bounds.Height * .42)) : 0);
        GitToolHost.IsVisible = GitSplitter.IsVisible = showGit;
        GitToolButton.IsChecked = showGit;
        GitTabTitle.Text = _tool == "log"
            ? LocalizedText.Format("git.editor.log_tab", _viewModel.Status?.Branch ?? "HEAD")
            : LocalizedText.Get(_tool switch { "branches" => "git.editor.branches", "remotes" => "git.page.remotes", _ => "git.page.overview" });
    }

    private void ShowTool()
    {
        if (!_tools.TryGetValue(_tool, out var tool))
        {
            tool = _tool switch
            {
                "branches" => new GitBranchesView(_viewModel),
                "remotes" => new GitRemotesView(_viewModel),
                "overview" => new GitOverviewView(_viewModel),
                _ => new GitLogView(_viewModel),
            };
            _tools[_tool] = tool;
        }
        ContentHost.Content = tool;
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
    private void New_Click(object? sender, RoutedEventArgs e) => _project.Editor.NewDocumentCommand.Execute(null);
    private async void OpenFile_Click(object? sender, RoutedEventArgs e) => await _project.Editor.OpenDocumentCommand.ExecuteAsync(null);
    private async void OpenProject_Click(object? sender, RoutedEventArgs e) => await _project.CloseProjectCommand.ExecuteAsync(null);
    private async void Save_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveCommand.ExecuteAsync(null);
    private async void SaveAll_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveAllCommand.ExecuteAsync(null);
    private async void SaveAs_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveAsCommand.ExecuteAsync(null);
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
    private void Branches_Click(object? sender, RoutedEventArgs e)
    {
        BranchBadge.Flyout?.Hide();
        OpenTool("branches");
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
        ShowTool();
        UpdateLayoutState();
    }
    private async void HeaderBranch_DoubleTapped(object? sender, TappedEventArgs e) => await CheckoutHeaderBranchAsync();
    private async void HeaderBranch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CheckoutHeaderBranchAsync();
    }
    private async Task CheckoutHeaderBranchAsync()
    {
        if (_viewModel.SelectedBranch is not { } branch || !_viewModel.CanManage || branch.IsCurrent) return;
        await _viewModel.CheckoutBranchCommand.ExecuteAsync(branch);
        BranchBadge.Flyout?.Hide();
    }
}
