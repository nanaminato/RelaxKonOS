using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Mobile.Navigation;
using RelaxKonOS.Client.Mobile.ViewModels;

namespace RelaxKonOS.Client.Mobile.Views;

public sealed partial class MobileShellView : UserControl
{
    public MobileShellView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        DataContextChanged += (_, _) => ApplyAuthenticationState();
    }

    private async void ConnectOnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MobileShellViewModel viewModel) return;
        await viewModel.ConnectAsync();
        ApplyAuthenticationState();
    }

    private void ApplyAuthenticationState()
    {
        var authenticated = (DataContext as MobileShellViewModel)?.IsAuthenticated == true;
        LoginPanel.IsVisible = !authenticated;
        ShellPanel.IsVisible = authenticated;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var state = MobileLayoutCalculator.FromAvailableWidth(Bounds.Width);
        WideNavigation.Width = state == MobileLayoutState.Medium ? 72 : 180;
        WideNavigation.IsVisible = state != MobileLayoutState.Compact;
        DetailPane.IsVisible = state == MobileLayoutState.Expanded;
        CompactNavigation.IsVisible = (DataContext as MobileShellViewModel)?.IsAuthenticated == true && state == MobileLayoutState.Compact;
    }
}
