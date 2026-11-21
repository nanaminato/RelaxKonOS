using Avalonia.Controls;

namespace RelaxKonOS.App.ProxyManager.Views;

internal partial class ProxySettingsView : UserControl
{
    public ProxySettingsView() => InitializeComponent();

    public ProxySettingsView(Control? dependencySettings) : this() => DependencySettingsHost.Content = dependencySettings;
}
