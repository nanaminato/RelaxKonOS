using Avalonia.VisualTree;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Settings.Views.Pages;

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
        if (DataContext is SystemPageViewModel model) _ = model.RefreshServerModulesAsync();
    }

    private void OnDailySettingsClick(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualAncestors().OfType<SettingsView>().FirstOrDefault()?.DataContext is SettingsViewModel model)
            model.OpenPageCommand.Execute("system/preferences");
    }
}
