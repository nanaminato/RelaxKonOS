using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentIdentityView : UserControl
{
    public ComponentIdentityView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? OpenApplicationRequested;

    private void OpenApplication_Click(object? sender, RoutedEventArgs e) =>
        OpenApplicationRequested?.Invoke(this, e);
}
