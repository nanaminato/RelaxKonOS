using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxyOverviewView : UserControl
{
    // Reserve the scrollbar's track plus a visual gutter. ScrollViewer otherwise measures
    // vertical content at infinite width, causing star-sized card columns to become Auto.
    private const double OverviewRightInset = 28;
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ProxyOverviewView()
    {
        InitializeComponent();
        _trafficTimer.Tick += TrafficTimer_Tick;
        OverviewScrollViewer.SizeChanged += OverviewScrollViewer_SizeChanged;
        AttachedToVisualTree += (_, _) =>
        {
            UpdateOverviewContentWidth();
            _trafficTimer.Start();
            _ = RefreshTrafficAsync();
        };
        DetachedFromVisualTree += (_, _) => _trafficTimer.Stop();
    }

    private async void TrafficTimer_Tick(object? sender, EventArgs eventArgs) => await RefreshTrafficAsync();

    private void OverviewScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateOverviewContentWidth();

    private void UpdateOverviewContentWidth()
    {
        var width = OverviewScrollViewer.Bounds.Width - OverviewRightInset;
        OverviewContent.Width = width > 0 ? width : double.NaN;
    }

    private void SubscriptionNavigation_PointerPressed(object? sender, PointerPressedEventArgs eventArgs) =>
        Navigate("profiles", eventArgs);

    private void ProxyNavigation_PointerPressed(object? sender, PointerPressedEventArgs eventArgs) =>
        Navigate("proxies", eventArgs);

    private void Navigate(string section, PointerPressedEventArgs eventArgs)
    {
        if (DataContext is ProxyManagerViewModel viewModel && viewModel.NavigateCommand.CanExecute(section))
            viewModel.NavigateCommand.Execute(section);
        eventArgs.Handled = true;
    }

    private Task RefreshTrafficAsync() => DataContext is ProxyManagerViewModel viewModel
        ? viewModel.RefreshTrafficAsync()
        : Task.CompletedTask;
}
