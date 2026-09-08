using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

/// <summary>Web Server Manager shell. Layout lives in AXAML; this class only switches pages.</summary>
internal partial class WebServerManagerWorkspace : UserControl
{
    private Button? _selectedButton;

    private WebServerManagerWorkspace(WebServerManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ShowPage("instances", InstancesPageButton);
    }

    public static Control Create(WebServerManagerViewModel viewModel) => new WebServerManagerWorkspace(viewModel);

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
        ContentHost.Content = page == "sites"
            ? new WebServerSitesPageView()
            : new WebServerInstancesPageView();
    }
}
