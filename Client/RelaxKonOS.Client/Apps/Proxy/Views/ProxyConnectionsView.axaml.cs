using Avalonia.Controls;
using Avalonia.Threading;
namespace RelaxKonOS.Client.Apps.Proxy.Views;
internal partial class ProxyConnectionsView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    public ProxyConnectionsView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        AttachedToVisualTree += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
    private Task RefreshAsync() => DataContext is ProxyManagerViewModel model ? model.RefreshConnectionsAsync() : Task.CompletedTask;
}
