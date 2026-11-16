using Avalonia.Controls;
using Avalonia.Interactivity;
namespace RelaxKonOS.Client.Apps.Databases.Views;
public partial class DatabaseInitializeDialog : UserControl
{
    private readonly Action<int> complete;
    public DatabaseInitializeDialog(Action<int> complete) { this.complete = complete; InitializeComponent(); }
    private void Accept(object? sender, RoutedEventArgs args) { if (Port.Value is >= 1024 and <= 65535) complete((int)Port.Value.Value); }
    private void Cancel(object? sender, RoutedEventArgs args) => complete(0);
}
