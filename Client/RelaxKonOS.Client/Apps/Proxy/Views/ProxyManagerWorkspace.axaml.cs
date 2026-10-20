using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Apps.ProxyServer.Views;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxyManagerWorkspace : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    private Button? _selectedButton;
    private readonly ProxyServerRuntimeViewModel? _runtime;
    private readonly ProxyServerRuntimeView? _runtimeView;

    public ProxyManagerWorkspace(ProxyManagerViewModel viewModel) : this(viewModel, null) { }

    public ProxyManagerWorkspace(ProxyManagerViewModel viewModel, ProxyServerRuntimeViewModel? runtime)
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        DataContext = viewModel;
        _runtime = runtime;
        _runtimeView = runtime is null ? null : new ProxyServerRuntimeView(runtime);
        RuntimeButton.IsVisible = runtime is not null;
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
            "runtime" when _runtimeView is not null => _runtimeView,
            "settings" => new ProxySettingsView(),
            _ => new ProxyOverviewView()
        };
        // The runtime page re-reads Host facts on entry; a running operation is never duplicated.
        if (section == "runtime") _ = _runtime?.EnsureLoadedAsync();
    }

    private Button? FindNavigationButton(string section) => section switch
    {
        "profiles" => ProfilesButton,
        "proxies" => ProxiesButton,
        "runtime" => RuntimeButton,
        "settings" => SettingsButton,
        _ => OverviewButton,
    };
}
