using Avalonia.Controls;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ServerCenter.Views;
public partial class ServerCenterHostsPage : UserControl
{
    public ServerCenterHostsPage(IServerCenterHostsPresentation presentation)
    {
        InitializeComponent();
        DataContext = presentation;
    }
}
