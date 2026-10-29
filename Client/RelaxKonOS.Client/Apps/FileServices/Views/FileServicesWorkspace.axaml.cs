using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesWorkspace : UserControl
{
    public void AttachWindowHeader(ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    private readonly FileServicesViewModel _viewModel;
    private Button? _selectedButton;

    public FileServicesWorkspace(FileServicesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ShowPage("overview", OverviewButton);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            EnsureAvailablePage();
            ApplyResponsiveLayout();
        };
        DetachedFromVisualTree += (_, _) => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ApplyResponsiveLayout()
    {
        var compact = Bounds.Width < 720;
        RootGrid.Margin = compact ? new Avalonia.Thickness(12) : new Avalonia.Thickness(20);
        WorkspaceGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "190,*");
        WorkspaceGrid.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
        WorkspaceGrid.ColumnSpacing = compact ? 0 : 16;
        WorkspaceGrid.RowSpacing = compact ? 10 : 0;
        NavigationPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        Grid.SetColumn(ContentHost, compact ? 0 : 1);
        Grid.SetRow(ContentHost, compact ? 1 : 0);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileServicesViewModel.SupportsSambaCredentials)) EnsureAvailablePage();
    }

    private void EnsureAvailablePage()
    {
        if (!_viewModel.SupportsSambaCredentials && _selectedButton == UsersButton) ShowPage("overview", OverviewButton);
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button) ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        if (page == "users" && !_viewModel.SupportsSambaCredentials) return;
        _selectedButton?.Classes.Remove("nav-selected");
        _selectedButton = button;
        button.Classes.Add("nav-selected");
        if (page == "settings" && _viewModel.Dependency is { } dependency) _ = dependency.RefreshAsync();
        Control pageContent = page switch
        {
            "shares" => new FileServicesSharesView(),
            "users" => new FileServicesUsersView(),
            "settings" => new RelaxKonOS.Client.Apps.ComponentCenter.ComponentDependencySettingsView
            {
                DataContext = _viewModel.Dependency
            },
            _ => new FileServicesOverviewView()
        };
        ContentHost.Content = pageContent is RelaxKonOS.Client.Apps.ComponentCenter.ComponentDependencySettingsView settings
            ? new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            : pageContent;
    }
}
