using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.Views.Login;
using RelaxKonOS.Protocol.ServerCenter;

AppBuilder.Configure<PickerTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var directory = Path.Combine(Path.GetTempPath(), "rk-login-selection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var targets = new HostTargetStore(directory);
    var keys = new SshHostKeyTrustStore(directory);
    var store = new LoginTunnelStore(directory);
    var tunnel = SshLoginTunnelProfile.Create("192.168.1.2", 22, "ssh-user", "https://127.0.0.1:5000");
    store.Save(tunnel);
    var vm = new LoginViewModel(DispatchProxy.Create<IAuthSession, SelectionAuthStub>(),
        new LoginLocalizationService(new LocalLanguageStore()),
        new ServerEndpointResolver(new HttpClient(), new ServerCertificateTrust(directory)),
        new SshDesktopSession(null!), targets, keys, new SshCredentialStore(directory),
        new ServerCenterConnectionResolver(keys, targets, new SshNetServerCenterTransportFactory()), store);
    var tunnelLogin = new SavedLoginProfile(tunnel.ServiceId, "server-user", "server-password", DateTimeOffset.UtcNow) { DisplayName = tunnel.DisplayText };
    var directLogin = new SavedLoginProfile("https://192.168.1.2:5000", "direct-user", null, DateTimeOffset.UtcNow);
    vm.SavedProfiles.Add(tunnelLogin);
    vm.SavedProfiles.Add(directLogin);
    var view = new LoginView { DataContext = vm };
    var window = new Window { Content = view, Width = 700, Height = 900 };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    var picker = view.FindControl<ComboBox>("SavedServerPicker")!;
    var address = view.FindControl<TextBox>("ServerAddressInput")!;
    Check(!picker.IsEditable, "Saved records do not write display summaries into the address editor.");
    picker.SelectedItem = tunnelLogin;
    Dispatcher.UIThread.RunJobs();
    Check(vm.UseLoginTunnel && address.Text == tunnel.RemoteUrl && vm.ServerUrl == tunnel.RemoteUrl,
        "Selecting a tunnel login fills its remote URL in the actual address control.");
    Check(vm.Identifier == "server-user" && vm.TunnelUserName == "ssh-user" && vm.TunnelHost == "192.168.1.2" && vm.TunnelPort == "22",
        "Server and SSH accounts are filled independently.");
    picker.SelectedItem = directLogin;
    Dispatcher.UIThread.RunJobs();
    Check(!vm.UseLoginTunnel && address.Text == directLogin.DirectServerUrl && vm.Identifier == "direct-user",
        "Selecting a direct login restores its address and account.");
    picker.SelectedItem = tunnelLogin;
    Dispatcher.UIThread.RunJobs();
    Check(vm.UseLoginTunnel && address.Text == tunnel.RemoteUrl && ReferenceEquals(vm.SelectedProfile, tunnelLogin),
        "Repeated selection retains the selected profile and remote URL.");
    address.Text = "https://127.0.0.1:5001";
    Dispatcher.UIThread.RunJobs();
    Check(vm.ServerUrl == address.Text && vm.UseLoginTunnel, "Manual remote-address edits remain editable without clearing tunnel mode.");
    var reuse = view.FindControl<CheckBox>("ReuseServerCredentialsToggle")!;
    var separate = view.FindControl<StackPanel>("SeparateSshCredentials")!;
    vm.TunnelUsePrivateKey = true;
    reuse.IsChecked = true;
    Dispatcher.UIThread.RunJobs();
    Check(vm.UseServerCredentialsForTunnel && !vm.TunnelUsePrivateKey && vm.ShowOptions && !separate.IsVisible,
        "Credential reuse hides separate SSH fields and makes Server credentials editable.");
    reuse.IsChecked = false;
    Dispatcher.UIThread.RunJobs();
    Check(!vm.UseServerCredentialsForTunnel && separate.IsVisible,
        "Turning reuse off shows the independent SSH credential fields.");
    window.Close();
}
finally { Directory.Delete(directory, true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
class PickerTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
class SelectionAuthStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "add_StateChanged" or "remove_StateChanged" => null,
        "get_State" => AuthSessionState.Unauthenticated,
        _ => throw new NotSupportedException(method?.Name)
    };
}
