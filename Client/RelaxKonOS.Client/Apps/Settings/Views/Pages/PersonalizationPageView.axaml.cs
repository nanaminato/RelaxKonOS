using Avalonia.Controls;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Settings.ViewModels;

namespace RelaxKonOS.Client.Apps.Settings.Views.Pages;

public partial class PersonalizationPageView : UserControl
{
    public PersonalizationPageView() => InitializeComponent();
    private void OnSectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Button { Tag: string route } && this.FindAncestorOfType<SettingsView>()?.DataContext is SettingsViewModel model)
            model.OpenPageCommand.Execute(route);
    }
}
