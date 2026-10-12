using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace RelaxKonOS.Client.Apps.Registry;

public partial class RegistryView : UserControl
{
    public RegistryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, Registry_KeyDown);
        RegistryTree.AddHandler(TreeViewItem.ExpandedEvent, TreeItemExpanded);
    }
    private void Registry_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RegistryViewModel vm) return;
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control) { AddressBox.Focus(); AddressBox.SelectAll(); }
        else if (e.Key == Key.F5) vm.RefreshCommand.Execute(null);
        else if (e.Key == Key.F2 && vm.CanRenameValue) vm.RenameSelectedCommand.Execute(null);
        else if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt) vm.MoveHistory(-1);
        else if (e.Key == Key.Right && e.KeyModifiers == KeyModifiers.Alt) vm.MoveHistory(1);
        else if (e.Source is not TextBox && e.Key == Key.Enter && vm.CanEditValue) vm.EditSelectedCommand.Execute(null);
        else if (e.Source is not TextBox && e.Key == Key.Delete && vm.CanDeleteValue) vm.DeleteCommand.Execute(null);
        else return;
        e.Handled = true;
    }
    private void EntriesGrid_DoubleTapped(object? sender, RoutedEventArgs e) =>
        (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void NewValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.NewValueCommand.Execute(null);
    private void ModifyValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.EditSelectedCommand.Execute(null);
    private void RenameValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.RenameSelectedCommand.Execute(null);
    private void ResetValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.ResetSelectedCommand.Execute(null);
    private void DeleteValue_Click(object? sender, RoutedEventArgs e) => (DataContext as RegistryViewModel)?.DeleteCommand.Execute(null);
    private void NavigationPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (DataContext as RegistryViewModel)?.NavigateCommand.Execute(null);
        e.Handled = true;
    }
    private async void TreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: RegistryKeyNode key })
        {
            try { await ((DataContext as RegistryViewModel)?.LoadChildrenAsync(key) ?? Task.CompletedTask); }
            catch (Exception error)
            {
                if (DataContext is RegistryViewModel vm && ReferenceEquals(vm.SelectedKey, key))
                    vm.StatusText = RelaxKonOS.Client.Localization.LocalizedText.RefWithFallback("registry.status.unavailable", "Registry unavailable: {0}", error.Message);
            }
        }
    }
}
