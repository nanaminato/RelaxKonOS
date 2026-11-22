using Avalonia.Interactivity;
using Avalonia.Controls;

namespace RelaxKonOS.App.Settings.Views;

public partial class SystemPageView : UserControl
{
    public SystemPageView() => InitializeComponent();

    /// <summary>
    /// Reads the module inventory when the page becomes visible. The catalog serves its cache, so
    /// revisiting the page does not re-request, and a failed read is shown by the page itself.
    /// </summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ISystemPage model) _ = model.RefreshServerModulesAsync();
    }

    public event EventHandler<string>? RouteRequested;

    private void OnDailySettingsClick(object? sender, RoutedEventArgs e)
    {
        RouteRequested?.Invoke(this, "system/preferences");
    }
}
