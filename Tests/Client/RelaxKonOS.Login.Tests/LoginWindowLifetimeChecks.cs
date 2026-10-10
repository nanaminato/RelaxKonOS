using System.Reflection;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.Views.Login;

internal static class LoginWindowLifetimeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-login-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var localization = new LoginLocalizationService(new LocalLanguageStore());
            using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
            var servicesProperty = typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!;
            var previous = servicesProperty.GetValue(null);
            servicesProperty.SetValue(null, services);
            try
            {
                using var http = new HttpClient(new WindowProbeHandler());
                var keys = new SshHostKeyTrustStore(directory);
                var targets = new HostTargetStore(directory);
                var vm = new LoginViewModel(DispatchProxy.Create<IAuthSession, WindowAuthStub>(), localization,
                    new ServerEndpointResolver(http, new ServerCertificateTrust(directory)), new SshDesktopSession(null!),
                    targets, keys, new SshCredentialStore(directory),
                    new ServerCenterConnectionResolver(keys, targets, new SshNetServerCenterTransportFactory()),
                    new LoginTunnelStore(directory)) { ServerUrl = "https://test.invalid:5000", Identifier = "test", Password = "test" };
                vm.StatusMessage = "Connected. Opening desktop...";
                vm.BeginWindowSession();
                check(vm.StatusMessage.Length == 0, "Reopened login clears the completed desktop handoff status.");
                var window = new LoginWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var connecting = vm.ConnectCommand.ExecuteAsync(null);
                check(vm.IsConnecting, "Actual login window has an active endpoint probe.");
                window.Close();
                PumpUntil(connecting);
                connecting.GetAwaiter().GetResult();
                check(!vm.IsConnecting && !vm.HasError, "Closing the actual login window cancels its active request.");

                vm.BeginWindowSession();
                window = new LoginWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                connecting = vm.ConnectCommand.ExecuteAsync(null);
                window.CloseForDesktop();
                Dispatcher.UIThread.RunJobs();
                check(!connecting.IsCompleted && vm.IsConnecting, "Desktop handoff closes the login window without canceling its request.");
                vm.CancelWindowOperations();
                PumpUntil(connecting);
                connecting.GetAwaiter().GetResult();
            }
            finally { servicesProperty.SetValue(null, previous); }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void PumpUntil(Task task)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return task.IsCompleted; }, 3000))
            throw new Exception("Window cancellation did not complete.");
    }
}

internal sealed class WindowProbeHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Probe must be canceled.");
    }
}
public class WindowAuthStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_State" => AuthSessionState.Unauthenticated,
        "add_StateChanged" or "remove_StateChanged" => null,
        _ => throw new NotSupportedException(method.Name)
    };
}
