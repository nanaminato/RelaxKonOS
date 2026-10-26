using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

internal partial class WebServerInstancesPageView : UserControl
{
    public WebServerInstancesPageView() => InitializeComponent();
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is WebServerManagerViewModel { IsLinuxServer: true })
        {
            LayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(ActionsPanel, 0);
        }
    }
}
