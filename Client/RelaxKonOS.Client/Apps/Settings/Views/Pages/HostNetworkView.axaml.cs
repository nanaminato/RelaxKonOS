using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.Settings.ViewModels;

namespace RelaxKonOS.Client.Apps.Settings.Views.Pages;

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
        if (sender is Button { DataContext: NetworkAdapterItem adapter } && DataContext is HostNetworkEditorViewModel editor)
            editor.OpenAdapter(adapter);
    }
}
