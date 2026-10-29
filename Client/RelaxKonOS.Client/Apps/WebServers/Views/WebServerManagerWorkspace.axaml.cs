using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

/// <summary>Web Server Manager shell. Layout lives in AXAML; this class only switches pages.</summary>
internal partial class WebServerManagerWorkspace : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window);
    }

    private Button? _selectedButton;

    private WebServerManagerWorkspace(WebServerManagerViewModel viewModel)
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        DataContext = viewModel;
        ShowPage("instances", InstancesPageButton);
    }

    public static WebServerManagerWorkspace Create(WebServerManagerViewModel viewModel) => new WebServerManagerWorkspace(viewModel);

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button)
            ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        if (_selectedButton is not null)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        _selectedButton = button;
        button.Classes.Add("nav-selected");
        var dependency = (DataContext as WebServerManagerViewModel)?.Dependency;
        if (page == "settings" && dependency is not null) _ = dependency.RefreshAsync();
        ContentHost.Content = page switch
        {
            "sites" => new WebServerSitesPageView(),
            "settings" => new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new RelaxKonOS.Client.Apps.ComponentCenter.ComponentDependencySettingsView { DataContext = dependency }
            },
            _ => new WebServerInstancesPageView()
        };
    }
}
