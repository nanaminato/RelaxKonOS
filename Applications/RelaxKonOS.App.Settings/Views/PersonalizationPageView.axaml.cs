using Avalonia.Controls;

namespace RelaxKonOS.App.Settings.Views;

public partial class PersonalizationPageView : UserControl
{
    public PersonalizationPageView() => InitializeComponent();

    public event EventHandler<string>? RouteRequested;

    private void OnSectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Button { Tag: string route })
            RouteRequested?.Invoke(this, route);
    }
}
