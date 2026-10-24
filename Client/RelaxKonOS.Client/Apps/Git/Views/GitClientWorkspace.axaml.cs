using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Project editor shell with persistent documents and independently toggled Git tools.</summary>
internal partial class GitClientWorkspace : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ProjectEditorViewModel _project;
    private RelaxKonOS.WindowManager.ManagedWindow? _headerWindow;
    private bool _attached;
    private string _tool = "log";
    private double _gitHeight = 300;
    private double _commitWidth = 300;
    private readonly Dictionary<string, Control> _tools = [];

    private GitClientWorkspace(GitClientViewModel viewModel, ProjectEditorViewModel project)
    {
        _viewModel = viewModel;
        _project = project;
        InitializeComponent();
        DataContext = viewModel;
        RefreshButton.Command = project.RefreshCommand;
        EditorPane.DataContext = project.Editor;
        EditorPane.UseProjectLayout();
        EditorArea.RowDefinitions[3].Height = new GridLength(0);
        Loaded += (_, _) =>
        {
            if (_attached) return;
            _attached = true;
            _viewModel.PropertyChanged += GitChanged;
            _project.PropertyChanged += ProjectChanged;
            if (_project.IsGitVisible) ShowTool();
            UpdateLayoutState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged -= GitChanged;
            _project.PropertyChanged -= ProjectChanged;
            _attached = false;
        };
        SizeChanged += (_, _) => UpdateLayoutState();
    }

    public static GitClientWorkspace Create(GitClientViewModel viewModel, ProjectEditorViewModel project) => new(viewModel, project);
    public RelaxKonOS.Client.Apps.CodeEditor.CodeEditorView EditorView => EditorPane;

    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        _headerWindow = window;
        if (_viewModel.IsPickerMode) return;
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
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
        UpdateLayoutState();
    }

    private void ProjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectEditorViewModel.IsGitVisible) && _project.IsGitVisible) ShowTool();
        UpdateLayoutState();
    }

    private void UpdateLayoutState()
    {
        InitializeButton.IsVisible = _viewModel.SelectedRepository is null;
        ProjectNameLabel.Text = _project.ProjectName;
        ProjectNameLabel.IsVisible = Bounds.Width >= 760;
        ProjectNameLabel.SetValue(ToolTip.TipProperty, _project.ProjectPath);
        BranchBadge.IsVisible = _viewModel.SelectedRepository is not null && Bounds.Width >= 600;
        var showCommit = _project.IsCommitVisible && _viewModel.SelectedRepository is not null;
        if (MainWorkspace.ColumnDefinitions[0].Width.Value > 0)
            _commitWidth = MainWorkspace.ColumnDefinitions[0].Width.Value;
        MainWorkspace.ColumnDefinitions[0].Width = new GridLength(showCommit ? Math.Min(_commitWidth, Math.Max(220, Bounds.Width * .45)) : 0);
        MainWorkspace.ColumnDefinitions[1].Width = new GridLength(showCommit ? 5 : 0);
        CommitTool.IsVisible = CommitSplitter.IsVisible = showCommit;
        var showGit = _project.IsGitVisible && _viewModel.SelectedRepository is not null;
        if (EditorArea.RowDefinitions[3].Height.Value > 0)
            _gitHeight = EditorArea.RowDefinitions[3].Height.Value;
        EditorArea.RowDefinitions[1].Height = new GridLength(showGit ? 5 : 0);
        EditorArea.RowDefinitions[3].Height = new GridLength(showGit ? Math.Min(_gitHeight, Math.Max(160, Bounds.Height * .45)) : 0);
        GitToolHost.IsVisible = GitSplitter.IsVisible = showGit;
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

    private void Project_Click(object? sender, RoutedEventArgs e) => _project.ToggleProjectCommand.Execute(null);
    private void Commit_Click(object? sender, RoutedEventArgs e) => _project.ToggleCommitCommand.Execute(null);
    private void New_Click(object? sender, RoutedEventArgs e)
    {
        if (_project.Editor.CanEditDocument) _project.Editor.NewDocumentCommand.Execute(null);
    }
    private async void Save_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveCommand.ExecuteAsync(null);
    private async void SaveAll_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveAllCommand.ExecuteAsync(null);
    private async void SaveAs_Click(object? sender, RoutedEventArgs e) => await _project.Editor.SaveAsCommand.ExecuteAsync(null);
    private void Find_Click(object? sender, RoutedEventArgs e) => EditorPane.FocusFind();
    private async void Initialize_Click(object? sender, RoutedEventArgs e) => await _project.InitializeGitCommand.ExecuteAsync(null);
    private async void Stage_Click(object? sender, RoutedEventArgs e)
    {
        if (_project.Editor.CurrentPath is not { } path || _project.ProjectPath is not { } root || !_viewModel.CanManage) return;
        var relative = path.Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + '/';
        if (!relative.StartsWith(prefix, StringComparison.Ordinal)) return;
        await _viewModel.StageFileContextCommand.ExecuteAsync(new RelaxKonOS.Protocol.Git.GitFileChangeDto(relative[prefix.Length..], Status: "modified"));
    }
    private void Branches_Click(object? sender, RoutedEventArgs e) => ToggleTool("branches");
    private void GitTool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tool }) ToggleTool(tool);
    }
    private void ToggleTool(string tool)
    {
        _project.IsGitVisible = !_project.IsGitVisible || _tool != tool;
        _tool = tool;
        if (_project.IsGitVisible) ShowTool();
        UpdateLayoutState();
    }
}
