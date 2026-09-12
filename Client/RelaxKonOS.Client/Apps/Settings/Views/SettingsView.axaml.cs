using Avalonia.Controls;
using Avalonia.Input;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 760;
            NavigationLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 210);
            Sidebar.IsVisible = !compact;
            CompactNavigation.IsVisible = compact;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            { SearchBox.Focus(); SearchBox.SelectAll(); args.Handled = true; }
            else if (args.Key == Key.Escape && DataContext is SettingsViewModel { HasSearch: true } model)
            { model.SearchQuery = ""; args.Handled = true; }
        };
    }

    private void OnSearchResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs args) => OpenSelectedResult(sender);

    private void OnSearchResultKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        OpenSelectedResult(sender);
        args.Handled = true;
    }

    private void OpenSelectedResult(object? sender)
    {
        if (sender is ListBox { SelectedItem: SettingsSearchEntry entry } list && DataContext is SettingsViewModel model)
        {
            list.SelectedItem = null;
            model.OpenSearchResultCommand.Execute(entry);
        }
    }
}
