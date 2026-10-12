using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Registry;

public partial class RegistryValueDialogView : UserControl
{
    public RegistryValueDialogView() => InitializeComponent();

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
