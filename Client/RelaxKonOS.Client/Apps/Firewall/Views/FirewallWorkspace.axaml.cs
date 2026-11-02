using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Firewall.Views;

/// <summary>
/// Firewall workspace. One host reading feeds both pages; the rules page keeps the table elastic so
/// a long rule set scrolls inside its own surface instead of pushing the actions out of view.
/// </summary>
public partial class FirewallWorkspace : UserControl
{
    private const int PageCount = 2;

    public FirewallWorkspace()
    {
        InitializeComponent();
        RelaxKonOS.Client.Controls.CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        ShowPage(0);
    }

    public FirewallWorkspace(FirewallViewModel viewModel) : this() => DataContext = viewModel;

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page)) ShowPage(page);
    }

    private void ShowPage(int page)
    {
        for (var index = 0; index < PageCount; index++)
        {
            this.FindControl<Control>($"Page{index}")!.IsVisible = index == page;
            this.FindControl<Button>($"PageButton{index}")!.Classes.Set("nav-selected", index == page);
        }
    }

    /// <summary>Moves the identity and refresh command into the host window's title bar.</summary>
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }
}
