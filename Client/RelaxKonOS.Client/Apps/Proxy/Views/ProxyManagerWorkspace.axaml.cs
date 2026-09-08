using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxyManagerWorkspace : UserControl
{
    private Button? _selectedButton;
    public ProxyManagerWorkspace(ProxyManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.NavigateRequested = section => Dispatcher.UIThread.Post(() => ShowPage(section, FindNavigationButton(section)));
        ShowPage("overview", OverviewButton);
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button) ShowPage(section, button);
    }

    private void ShowPage(string section, Button? button)
    {
        if (_selectedButton is not null) _selectedButton.Classes.Remove("nav-selected");
        _selectedButton = button;
        _selectedButton?.Classes.Add("nav-selected");
        ContentHost.Content = section switch
        {
            "profiles" => new ProxyProfilesView(),
            "proxies" => new ProxyGroupsView(),
            "connections" => new ProxyConnectionsView(),
            "logs" => new ProxyLogsView(),
            "settings" => new ProxySettingsView(),
            _ => new ProxyOverviewView()
        };
    }

    private Button? FindNavigationButton(string section) => section switch
    {
        "profiles" => ProfilesButton,
        "proxies" => ProxiesButton,
        "settings" => SettingsButton,
        _ => OverviewButton,
    };
}
