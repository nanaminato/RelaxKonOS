using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.Registry;

public partial class RegistryValueDialogView : UserControl
{
    public IRegistryAppearancePreferences? AppearancePreferences { get; init; }
    public RegistryValueDialogView() => InitializeComponent();
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (AppearancePreferences is null) return;
        AppearancePreferences.Changed += ApplyAppearance;
        ApplyAppearance(null, EventArgs.Empty);
    }
    private void ApplyAppearance(object? sender, EventArgs args)
    {
        if (AppearancePreferences is not { } preferences) return;
        Resources["RegistryScrollbarThickness"] = (double)preferences.Registry.ScrollbarThickness;
        Resources["RegistryTreeRowHeight"] = (double)preferences.Registry.TreeRowHeight;
        Resources["RegistryIconSize"] = (double)preferences.Registry.IconSize;
    }
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (AppearancePreferences is not null) AppearancePreferences.Changed -= ApplyAppearance;
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
