using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.ProxyServer.Views;

/// <summary>Managed runtime page hosted inside the Proxy Server workspace.</summary>
public partial class ProxyServerRuntimeView : UserControl
{
    public ProxyServerRuntimeView() => InitializeComponent();

    public ProxyServerRuntimeView(ProxyServerRuntimeViewModel viewModel) : this() => DataContext = viewModel;
}
