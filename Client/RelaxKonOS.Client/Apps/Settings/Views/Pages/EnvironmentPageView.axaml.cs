using Avalonia.Controls;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
namespace RelaxKonOS.Client.Apps.Settings.Views.Pages;
public partial class EnvironmentPageView : UserControl
{
    private bool _initialized;
    public EnvironmentPageView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialized || DataContext is not EnvironmentPageViewModel viewModel) return;
            _initialized = true;
            await viewModel.InitializeAsync();
        };
    }
}
