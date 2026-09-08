using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Tunnels.Views;

/// <summary>Shell for the tunnel manager. The individual views inherit one shared view model.</summary>
public partial class TunnelManagerView : UserControl
{
    private Button? _selectedButton;
    public TunnelManagerView()
    {
        InitializeComponent();
        ShowPage("overview", OverviewButton);
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button) ShowPage(section, button);
    }

    private void ShowPage(string section, Button button)
    {
        _selectedButton?.Classes.Remove("nav-selected");
        _selectedButton = button;
        button.Classes.Add("nav-selected");
        Control page = section switch
        {
            "tunnels" => new TunnelDefinitionsView(),
            "servers" => new TunnelServersView(),
            "frps" => new TunnelManagedFrpsView(),
            "runtime" => new TunnelRuntimeView(),
            _ => new TunnelOverviewView()
        };
        // Data grids need the actual viewport height so they can expand and scroll internally.
        // The remaining, document-like pages retain the previous outer scrolling behavior.
        ContentHost.Content = section is "tunnels" or "servers"
            ? page
            : new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = page
            };
    }
}
