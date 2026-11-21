using RelaxKonOS.App.ProxyManager;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Apps.Proxy.Views;

internal partial class ProxyOverviewView : UserControl
{
    // Reserve the scrollbar's track plus a visual gutter. ScrollViewer otherwise measures
    // vertical content at infinite width, causing star-sized card columns to become Auto.
    private const double OverviewRightInset = 28;
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _trafficLifetime;

    public ProxyOverviewView()
    {
        InitializeComponent();
        _trafficTimer.Tick += TrafficTimer_Tick;
        OverviewScrollViewer.SizeChanged += OverviewScrollViewer_SizeChanged;
        AttachedToVisualTree += (_, _) =>
        {
            UpdateOverviewContentWidth();
            UpdateResponsiveColumns();
            _trafficLifetime = new();
            _trafficTimer.Start();
            _ = RefreshTrafficAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _trafficTimer.Stop();
            using var lifetime = _trafficLifetime;
            _trafficLifetime = null;
            lifetime?.Cancel();
        };
    }

    private async void TrafficTimer_Tick(object? sender, EventArgs eventArgs) => await RefreshTrafficAsync();

    private void OverviewScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        UpdateOverviewContentWidth();
        UpdateResponsiveColumns();
    }

    private void UpdateOverviewContentWidth()
    {
        var width = OverviewScrollViewer.Bounds.Width - OverviewRightInset;
        OverviewContent.Width = width > 0 ? width : double.NaN;
    }

    // Two-column cards become one column below the width where the workspace hides its rail. The
    // breakpoint follows the measured page, not the window, because the rail and the page margins
    // consume a different amount of width at every window size.
    private bool? _wideColumns;

    private void UpdateResponsiveColumns()
    {
        var width = OverviewScrollViewer.Bounds.Width;
        if (width <= 0) return;
        var wide = width >= 720;
        if (_wideColumns == wide) return;
        _wideColumns = wide;
        StackPair(OverviewPairSubscriptions, CurrentNodeCard, wide);
        StackPair(OverviewPairNetwork, RoutingCard, wide);
        StackPair(OverviewPairFacts, SystemFactsCard, wide);
        StackHero(OverviewHero, HeroBody, width >= 560);
    }

    private static void StackPair(Grid pair, Control trailing, bool wide)
    {
        pair.RowDefinitions.Clear();
        pair.ColumnDefinitions = wide ? new ColumnDefinitions("*,*") : new ColumnDefinitions("*");
        if (wide)
        {
            Grid.SetRow(trailing, 0);
            Grid.SetColumn(trailing, 1);
            trailing.Margin = default;
            return;
        }

        pair.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        pair.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(trailing, 1);
        Grid.SetColumn(trailing, 0);
        trailing.Margin = new Thickness(0, 14, 0, 0);
    }

    private static void StackHero(Grid host, Control body, bool sideBySide)
    {
        host.RowDefinitions.Clear();
        host.ColumnDefinitions = sideBySide ? new ColumnDefinitions("Auto,*") : new ColumnDefinitions("*");
        if (sideBySide)
        {
            Grid.SetRow(body, 0);
            Grid.SetColumn(body, 1);
            body.Margin = default;
            return;
        }

        host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(body, 1);
        Grid.SetColumn(body, 0);
        body.Margin = new Thickness(0, 16, 0, 0);
    }

    // The overview cards cross-link to the pages that own them. These are commands, not pointer
    // handlers on a Border, so the shortcut is reachable from the keyboard and states its own role.
    private void SubscriptionNavigation_Click(object? sender, RoutedEventArgs eventArgs) => Navigate("profiles", eventArgs);

    private void ProxyNavigation_Click(object? sender, RoutedEventArgs eventArgs) => Navigate("proxies", eventArgs);

    private void Navigate(string section, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProxyManagerViewModel viewModel && viewModel.NavigateCommand.CanExecute(section))
            viewModel.NavigateCommand.Execute(section);
        eventArgs.Handled = true;
    }

    private Task RefreshTrafficAsync() => _trafficLifetime is { } lifetime && DataContext is ProxyManagerViewModel viewModel
        ? viewModel.RefreshTrafficAsync(lifetime.Token)
        : Task.CompletedTask;
}
