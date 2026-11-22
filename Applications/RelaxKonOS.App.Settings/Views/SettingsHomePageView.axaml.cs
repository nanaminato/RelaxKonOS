using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Settings.Views;

public partial class SettingsHomePageView : UserControl
{
    public SettingsHomePageView() => InitializeComponent();

    public event EventHandler<string>? RouteRequested;

    private void OnRouteClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string route })
            RouteRequested?.Invoke(this, route);
    }
}
