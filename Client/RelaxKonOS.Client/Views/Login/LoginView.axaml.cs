using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.ViewModels.Login;

namespace RelaxKonOS.Client.Views.Login;

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    private async void ServerAddress_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel && !viewModel.IsConnecting)
            await viewModel.DiscoverServerEndpointAsync();
    }

    private void RelaxLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel login) login.UseSshLogin = false;
    }

    private void SshLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel login) login.UseSshLogin = true;
    }
}
