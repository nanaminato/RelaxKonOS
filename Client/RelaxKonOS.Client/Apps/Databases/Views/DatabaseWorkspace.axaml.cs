using Avalonia.Controls;
using Avalonia.Interactivity;
namespace RelaxKonOS.Client.Apps.Databases.Views;
public partial class DatabaseWorkspace : UserControl
{
    public DatabaseWorkspace() => InitializeComponent();
    private void Overview(object? sender, RoutedEventArgs args) => Page.Content = new DatabaseOverview { DataContext = DataContext };
    private void Accounts(object? sender, RoutedEventArgs args) => Page.Content = new DatabaseAccounts { DataContext = (DataContext as DatabaseViewModel)?.AccountManager };
    private void RemoteAccess(object? sender, RoutedEventArgs args) => Page.Content = new DatabaseRemoteAccess { DataContext = (DataContext as DatabaseViewModel)?.RemoteManager };
    private void Maintenance(object? sender, RoutedEventArgs args) => Page.Content = new DatabaseMaintenance { DataContext = DataContext };
}
