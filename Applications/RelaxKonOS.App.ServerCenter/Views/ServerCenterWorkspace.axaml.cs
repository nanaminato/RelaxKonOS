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

    private readonly IServerCenterWorkspacePresentation _presentation;
    private readonly Func<Control> _createDeploymentPage;
    private Button? _selectedButton;

    public ServerCenterWorkspace(IServerCenterWorkspacePresentation presentation, Func<Control> createDeploymentPage)
    {
        InitializeComponent();
        _presentation = presentation;
        _createDeploymentPage = createDeploymentPage;
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
        ContentHost.Content = page switch
        {
            "deployment" => _createDeploymentPage(),
            "history" => new ServerCenterHistoryPage(_presentation.History),
            _ => new ServerCenterHostsPage(_presentation.Hosts),
        };
    }
}
