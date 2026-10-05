using Avalonia.VisualTree;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Settings.Views.Pages;

public partial class SystemPageView : UserControl
{
    public SystemPageView() => InitializeComponent();
    private void OnDailySettingsClick(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualAncestors().OfType<SettingsView>().FirstOrDefault()?.DataContext is SettingsViewModel model)
            model.OpenPageCommand.Execute("system/preferences");
    }
}
