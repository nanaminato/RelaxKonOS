using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;

internal static class LoginDiscoveryCancellationChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-login-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient(new PendingProbeHandler());
            var keys = new SshHostKeyTrustStore(directory);
            var targets = new HostTargetStore(directory);
            var vm = new LoginViewModel(DispatchProxy.Create<IAuthSession, DiscoveryAuthStub>(),
                new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(http, new ServerCertificateTrust(directory)), new SshDesktopSession(null!),
                targets, keys, new SshCredentialStore(directory),
                new ServerCenterConnectionResolver(keys, targets, new SshNetServerCenterTransportFactory()),
                new LoginTunnelStore(directory)) { ServerUrl = "https://probe.test:5000", Identifier = "test", Password = "test" };
            var commands = new List<IAsyncRelayCommand> { vm.ConnectCommand, vm.ConnectOwnerDeviceCommand };
            if (OperatingSystem.IsWindows()) commands.Add(vm.BootstrapWindowsOwnerDeviceCommand);
            foreach (var command in commands)
            {
                var connecting = command.ExecuteAsync(null);
                Check(vm.IsConnecting && vm.IsDiscoveringServer, "Endpoint discovery locks the login form from the start.");
                Check(!vm.ConnectCommand.CanExecute(null) && !vm.ConnectOwnerDeviceCommand.CanExecute(null)
                    && !vm.AcceptOwnerDevicePairingCommand.CanExecute(null), "Other login flows cannot start during discovery.");
                command.Cancel();
                await connecting;
                Check(!vm.IsConnecting && !vm.IsDiscoveringServer && !vm.HasError,
                    "Canceled discovery restores the form without reporting authentication failure.");
            }
            vm.BeginWindowSession();
            var closedWindowRequest = vm.ConnectCommand.ExecuteAsync(null);
            vm.CancelWindowOperations();
            await closedWindowRequest;
            Check(!vm.IsConnecting && !vm.HasError, "Closing the window cancels active endpoint discovery.");
            vm.BeginWindowSession();
            var newWindowRequest = vm.ConnectCommand.ExecuteAsync(null);
            Check(vm.IsConnecting, "A reopened login window receives a fresh cancellation lifetime.");
            vm.CancelWindowOperations();
            await newWindowRequest;
            vm.BeginWindowSession();
            var focusProbe = vm.DiscoverServerEndpointAsync();
            vm.CancelWindowOperations();
            await focusProbe;
            Check(!vm.IsDiscoveringServer && !vm.HasError, "Closing cancels focus-loss discovery without an unhandled error.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

internal sealed class PendingProbeHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Canceled probe must not complete.");
    }
}

public class DiscoveryAuthStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_State" => AuthSessionState.Unauthenticated,
        "add_StateChanged" or "remove_StateChanged" => null,
        _ => throw new NotSupportedException(method.Name)
    };
}
