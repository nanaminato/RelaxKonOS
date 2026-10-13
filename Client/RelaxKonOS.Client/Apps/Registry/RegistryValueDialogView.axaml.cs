using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Registry;

public partial class RegistryValueDialogView : UserControl
{
    private RelaxKonOS.Client.Services.ClientPresentationPreferences? _preferences;
    public RegistryValueDialogView() => InitializeComponent();
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _preferences = App.Services.GetService(typeof(RelaxKonOS.Client.Services.ClientPresentationPreferences)) as RelaxKonOS.Client.Services.ClientPresentationPreferences;
        if (_preferences is null) return;
        _preferences.Changed += ApplyAppearance;
        _preferences.Activate();
        ApplyAppearance(null, EventArgs.Empty);
    }
    private void ApplyAppearance(object? sender, EventArgs args)
    {
        if (_preferences is null) return;
        Resources["RegistryScrollbarThickness"] = (double)_preferences.Registry.ScrollbarThickness;
        Resources["RegistryTreeRowHeight"] = (double)_preferences.Registry.TreeRowHeight;
        Resources["RegistryIconSize"] = (double)_preferences.Registry.IconSize;
    }
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_preferences is not null)
        {
            _preferences.Changed -= ApplyAppearance;
            _preferences.Deactivate();
            _preferences = null;
        }
        base.OnUnloaded(e);
    }

    private void RemoveListItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RegistryListItem item } && DataContext is RegistryValueDialogViewModel vm)
            vm.RemoveListItemCommand.Execute(item);
    }

    private void PickColor_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color } && DataContext is RegistryValueDialogViewModel vm)
            vm.PickColorCommand.Execute(color);
    }
}
