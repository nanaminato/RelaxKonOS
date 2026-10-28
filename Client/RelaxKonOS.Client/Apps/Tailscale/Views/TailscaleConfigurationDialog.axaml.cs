using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Tailscale;
namespace RelaxKonOS.Client.Apps.Tailscale.Views;
public partial class TailscaleConfigurationDialog : UserControl
{
    private readonly Action<TailscaleConfiguration?> _complete;
    private readonly TailscaleConfigurationEditor _model;
    public TailscaleConfigurationDialog(TailscaleConfiguration configuration, Action<TailscaleConfiguration?> complete)
    {
        InitializeComponent(); _complete = complete;
        DataContext = _model = new() { AcceptDns = configuration.AcceptDns, AcceptRoutes = configuration.AcceptRoutes };
    }
    private void Cancel(object? sender, RoutedEventArgs e) => _complete(null);
    private void Preview(object? sender, RoutedEventArgs e) => _complete(new(_model.AcceptDns, _model.AcceptRoutes));
}
public sealed partial class TailscaleConfigurationEditor : ObservableObject
{
    [ObservableProperty] private bool _acceptDns;
    [ObservableProperty] private bool _acceptRoutes;
}
