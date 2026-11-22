using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ServerModulesHeaderView : UserControl
{
    public ServerModulesHeaderView() => InitializeComponent();

    /// <summary>
    /// A page-level tab changes what the body means, so it must not inherit the previous tab's
    /// reading position. The server-module page lives in the workspace's details scroller; reset
    /// that scroller instead of leaving the user halfway down a different section.
    /// </summary>
    private void SectionTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller) scroller.Offset = new Vector(0, 0);
    }
}
