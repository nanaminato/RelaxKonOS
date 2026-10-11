using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.PortForwarding.Views;

/// <summary>Docker-style workspace shell for the local port forward manager.</summary>
public partial class PortForwardingMainView : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    private Button? _selectedButton;

    public PortForwardingMainView()
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        ShowPage("forwards", ForwardsButton);
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
        ContentHost.Content = page == "connection"
            ? new PortForwardingConnectionView()
            : new PortForwardingForwardsView();
    }
}
