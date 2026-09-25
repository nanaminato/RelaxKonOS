using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Views.ServerCenter;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Views.Login;

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    private async void ServerAddress_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel && !viewModel.IsConnecting)
            await viewModel.DiscoverServerEndpointAsync();
    }

    private async void OpenServerCenter_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = App.Services.GetRequiredService<ServerCenterViewModel>();
        await viewModel.LoadAsync();
        if (DataContext is LoginViewModel currentLogin)
        {
            viewModel.UseSshLogin = currentLogin.UseSshLogin;
            viewModel.SelectedHost = viewModel.Hosts.FirstOrDefault(host => host.HostId == currentLogin.SshTarget?.HostId);
        }
        var window = new ServerCenterWindow { DataContext = viewModel };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
        if (DataContext is LoginViewModel login)
            login.SelectLoginMode(viewModel.UseSshLogin, viewModel.SelectedHost);
    }
}
