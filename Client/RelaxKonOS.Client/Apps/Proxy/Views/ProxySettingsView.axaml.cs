using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxySettingsView : UserControl
{
    public ProxySettingsView() => InitializeComponent();

    public ProxySettingsView(Control? dependencySettings) : this() => DependencySettingsHost.Content = dependencySettings;
}
