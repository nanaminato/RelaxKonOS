using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace RelaxKonOS.Client.Apps.Tailscale.Views;

public partial class TailscaleWorkspace : UserControl
{
    private Button? _selected;
    public TailscaleWorkspace(TailscaleViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Show("devices", Devices);
        AttachedToVisualTree += async (_, _) => await model.ActivateAsync();
        DetachedFromVisualTree += (_, _) => model.Deactivate();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 720;
            Body.ColumnDefinitions = new(compact ? "*" : "170,*");
            Body.RowDefinitions = new(compact ? "Auto,*" : "*");
            Body.RowSpacing = compact ? 10 : 0;
            Navigation.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            Grid.SetColumn(Page, compact ? 0 : 1); Grid.SetRow(Page, compact ? 1 : 0);
        };
    }
    private void Navigate(object? sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string page } button) Show(page, button); }
    private void Show(string page, Button button)
    {
        _selected?.Classes.Remove("nav-selected"); _selected = button; button.Classes.Add("nav-selected");
        Page.Content = page switch { "control" => new TailscaleControlPage(), "operations" => new TailscaleOperationsPage(), _ => new TailscaleDevicesPage() };
    }
}
