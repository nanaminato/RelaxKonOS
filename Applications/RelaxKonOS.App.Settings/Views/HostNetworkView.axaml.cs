using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Settings.Views;

public partial class HostNetworkView : UserControl
{
    public static readonly StyledProperty<bool> IsAdapterDetailPageProperty =
        AvaloniaProperty.Register<HostNetworkView, bool>(nameof(IsAdapterDetailPage));
    public bool IsAdapterDetailPage
    {
        get => GetValue(IsAdapterDetailPageProperty);
        set => SetValue(IsAdapterDetailPageProperty, value);
    }
    public HostNetworkView() => InitializeComponent();
    private void OnAdapterClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: INetworkAdapterItem adapter } && DataContext is IHostNetworkEditor editor)
            editor.OpenAdapter(adapter);
    }
}
