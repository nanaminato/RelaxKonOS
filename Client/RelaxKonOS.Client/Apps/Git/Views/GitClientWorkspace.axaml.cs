using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Git Client shell. Layout lives in AXAML; this class switches pages and handles navigation.
/// 支持两种显示模式：IsPickerMode=true 时显示项目选择器；否则显示当前仓库的工作区。
/// 通过监听 VM 的 ActivePage / IsPickerMode 属性变化来同步工作区 ContentHost，保证与 VM 状态一致。</summary>
internal partial class GitClientWorkspace : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private Button? _selectedButton;
    private bool _attached;

    private GitClientWorkspace(GitClientViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // 构造时 ContentHost 可能尚未注入（NameScope 未构建完整），等到 Loaded 再同步初始页面。
        // 同时 IsPickerMode=true 时导航栏默认隐藏，ContentHost 也不可见。
        Loaded += OnLoaded;
    }

    public static Control Create(GitClientViewModel viewModel) => new GitClientWorkspace(viewModel);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // 非选择器模式（例如重启后保持的模式），则按当前 ActivePage 初始化
        if (!_viewModel.IsPickerMode)
            SyncPageFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // IsPickerMode=false 切换为工作区：显示默认概览页 + 选中“概览”按钮
        if (e.PropertyName == nameof(GitClientViewModel.IsPickerMode) && !_viewModel.IsPickerMode)
        {
            _viewModel.ActivePage = GitClientPage.Overview;
            SyncPageFromViewModel();
        }
        else if (e.PropertyName == nameof(GitClientViewModel.ActivePage) && !_viewModel.IsPickerMode)
        {
            SyncPageFromViewModel();
        }
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section } btn) return;
        var page = SectionToPage(section);
        if (page.HasValue) _viewModel.ActivePage = page.Value;
        ShowPage(section, btn);
    }

    private void SyncPageFromViewModel()
    {
        if (ContentHost is null) return;
        var section = PageToSection(_viewModel.ActivePage);
        ContentHost.Content = CreatePageView(section);
        HighlightNavButton(section);
    }

    private void HighlightNavButton(string section)
    {
        // 找左侧导航栏所有 Tag 为 string 的 Button（AXAML 里的导航按钮都在左侧 StackPanel 中）
        var navButtons = this.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Tag is string)
            .ToList();

        if (_selectedButton is not null && _selectedButton.Tag is string)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        var target = navButtons.FirstOrDefault(b => string.Equals((string)b.Tag!, section, StringComparison.Ordinal));
        if (target is not null)
        {
            target.Classes.Add("nav-selected");
            _selectedButton = target;
        }
        else
        {
            _selectedButton = null;
        }
    }

    private void ShowPage(string section, Button? sourceButton = null)
    {
        if (_selectedButton is not null && _selectedButton != sourceButton)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        if (sourceButton is not null)
        {
            sourceButton.Classes.Add("nav-selected");
            _selectedButton = sourceButton;
        }

        if (ContentHost is not null)
            ContentHost.Content = CreatePageView(section);
    }

    private object CreatePageView(string section) => section switch
    {
        "workspace" => new GitWorkspaceView(_viewModel),
        "log" => new GitLogView(_viewModel),
        "remotes" => new GitRemotesView(_viewModel),
        "conflicts" => new GitConflictResolutionView(_viewModel),
        _ => new GitOverviewView(_viewModel),
    };

    private static string PageToSection(GitClientPage page) => page switch
    {
        GitClientPage.Workspace => "workspace",
        GitClientPage.Log => "log",
        GitClientPage.Remotes => "remotes",
        GitClientPage.ConflictResolution => "conflicts",
        _ => "overview",
    };

    private static GitClientPage? SectionToPage(string section) => section switch
    {
        "workspace" => GitClientPage.Workspace,
        "log" => GitClientPage.Log,
        "remotes" => GitClientPage.Remotes,
        "conflicts" => GitClientPage.ConflictResolution,
        "overview" => GitClientPage.Overview,
        _ => null,
    };
}
