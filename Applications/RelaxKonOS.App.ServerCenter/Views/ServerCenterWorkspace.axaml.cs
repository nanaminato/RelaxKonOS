using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.AppSDK;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.ServerCenter.Views;

/// <summary>Desktop-hosted server manager with the same sidebar navigation model as Docker Manager.</summary>
public partial class ServerCenterWorkspace : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window);
    }

    private readonly Func<ServerCenterWorkspacePage, Control> _createPage;
    private Button? _selectedButton;

    public ServerCenterWorkspace(IServerCenterWorkspacePresentation presentation, Func<ServerCenterWorkspacePage, Control> createPage)
    {
        InitializeComponent();
        _createPage = createPage;
        DataContext = presentation;
        RelaxKonOS.WindowManager.CompactWorkspaceNavigation.Install(this, WorkspaceBody,
            new Binding(nameof(IServerCenterWorkspacePresentation.NavigationMenuTitle)) { Source = presentation });
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
        ContentHost.Content = _createPage(page switch
        {
            "deployment" => ServerCenterWorkspacePage.Deployment,
            "history" => ServerCenterWorkspacePage.History,
            _ => ServerCenterWorkspacePage.Hosts,
        });
    }
}
