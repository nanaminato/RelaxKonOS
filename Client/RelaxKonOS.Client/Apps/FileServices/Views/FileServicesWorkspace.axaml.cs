using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

internal partial class FileServicesWorkspace : UserControl
{
    private readonly FileServicesViewModel _viewModel;
    private Button? _selectedButton;

    public FileServicesWorkspace(FileServicesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ShowPage("overview", OverviewButton);
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            EnsureAvailablePage();
        };
        DetachedFromVisualTree += (_, _) => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileServicesViewModel.SupportsSambaCredentials)) EnsureAvailablePage();
    }

    private void EnsureAvailablePage()
    {
        if (!_viewModel.SupportsSambaCredentials && _selectedButton == UsersButton) ShowPage("overview", OverviewButton);
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button) ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        if (page == "users" && !_viewModel.SupportsSambaCredentials) return;
        _selectedButton?.Classes.Remove("nav-selected");
        _selectedButton = button;
        button.Classes.Add("nav-selected");
        ContentHost.Content = page switch
        {
            "shares" => new FileServicesSharesView(),
            "users" => new FileServicesUsersView(),
            _ => new FileServicesOverviewView()
        };
    }
}
