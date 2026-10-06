using Avalonia.Controls;
using Avalonia.Input;
using RelaxKonOS.Client.Apps.Settings.ViewModels;

namespace RelaxKonOS.Client.Apps.Settings.Views;

public partial class SettingsHeaderView : UserControl
{
    public SettingsHeaderView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => HeaderTitle.IsVisible = args.NewSize.Width >= 360;
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            { FocusSearch(); args.Handled = true; }
            else if (args.Key == Key.Escape && DataContext is SettingsViewModel { HasSearch: true } model)
            { model.SearchQuery = ""; args.Handled = true; }
        };
    }
    public Action? BeforeFocusSearch { get; set; }
    public void FocusSearch() { BeforeFocusSearch?.Invoke(); HeaderSearchBox.Focus(); HeaderSearchBox.SelectAll(); }
}
