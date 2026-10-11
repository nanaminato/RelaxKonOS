using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

/// <summary>Desktop-hosted server manager with the same sidebar navigation model as Docker Manager.</summary>
public partial class ServerCenterWorkspace : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window);
    }

    private Button? _selectedButton;

    public ServerCenterWorkspace()
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        ShowPage("hosts", HostsButton);
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button)
            ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        _selectedButton?.Classes.Remove("nav-selected");
        _selectedButton = button;
        button.Classes.Add("nav-selected");
        ContentHost.Content = page switch
        {
            "deployment" => new ServerCenterDeploymentPage(),
            "history" => new ServerCenterHistoryPage(),
            _ => new ServerCenterHostsPage(),
        };
    }
}
