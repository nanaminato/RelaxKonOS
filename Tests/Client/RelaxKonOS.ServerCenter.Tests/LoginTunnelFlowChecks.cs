using System.Net;
using System.Reflection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;

static class LoginTunnelFlowChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-tunnel-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var targets = new HostTargetStore(directory);
            var keys = new SshHostKeyTrustStore(directory);
            var factory = new TunnelTestFactory();
            var auth = DispatchProxy.Create<IAuthSession, TunnelAuthProxy>();
            var authState = (TunnelAuthProxy)auth;
            var resolver = new ServerCenterConnectionResolver(keys, targets, factory);
            var vm = new LoginViewModel(auth, new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient(new TunnelProbeHandler()), new ServerCertificateTrust(directory)),
                new SshDesktopSession(null!), targets, keys,
                DispatchProxy.Create<ISshCredentialStore, TunnelCredentialProxy>(), resolver, new LoginTunnelStore(directory))
            {
                UseLoginTunnel = true, TunnelHost = "example.com", TunnelUserName = "ssh-user",
                TunnelSecret = "ssh-password", ServerUrl = "http://127.0.0.1:5000",
                Identifier = "server-user", Password = "server-password", RememberServer = false, RememberSshCredential = false,
                ConfirmTunnelHostKeyAsync = (_, _) => Task.FromResult(false)
            };
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert(authState.Logins == 0 && factory.Transports.All(t => t.Disposed), "Rejecting a host key sends no Server credentials and releases SSH.");
            vm.TunnelSecret = "ssh-password";
            vm.ConfirmTunnelHostKeyAsync = (_, _) => Task.FromResult(true);
            await vm.TestTunnelCommand.ExecuteAsync(null);
            Assert(!vm.HasError && factory.Transports.All(t => t.Disposed) && authState.Logins == 0, "Testing connects without signing in and closes its tunnel.");
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert(!vm.HasError && authState.Logins == 1 && factory.Transports.Last().Connected,
                "Connecting after a test reuses the verified SSH credential and holds the tunnel.");
            Assert(authState.Identity?.Kind == ServerServiceIdKind.SshTunnelProfile && authState.Identity.ServiceId.StartsWith("ssh-tunnel:") &&
                authState.Identity.EffectiveBaseUrl == "http://127.0.0.1:51000", "Only the transport address contains the local port.");
            authState.SignOut();
            Assert(factory.Transports.Last().Disposed, "Signing out closes the login tunnel and SSH connection.");
            vm.TunnelSecret = "ssh-password";
            authState.FailLogin = true;
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert(vm.HasError && factory.Transports.Last().Disposed, "A failed Server login releases its tunnel.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

class TunnelTestFactory : IServerCenterSshTransportFactory
{
    public List<TunnelTransportProxy> Transports { get; } = [];
    public IServerCenterSshTransport Create()
    {
        var transport = DispatchProxy.Create<IServerCenterSshTransport, TunnelTransportProxy>();
        Transports.Add((TunnelTransportProxy)transport);
        return transport;
    }
}
class TunnelTransportProxy : DispatchProxy
{
    public bool Connected;
    public bool Disposed;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "ConnectAsync":
                var endpoint = (ServerCenterSshEndpoint)args![0]!;
                var observation = new ServerCenterHostKeyObservation(endpoint.Host, endpoint.Port, "ssh-ed25519", [1, 2, 3]);
                var trust = ((Func<ServerCenterHostKeyObservation, ServerHostKeyTrust>)args[2]!)(observation);
                if (trust != ServerHostKeyTrust.Trusted) throw new ServerCenterHostKeyRejectedException(observation, trust);
                Connected = true; return Task.CompletedTask;
            case "get_IsConnected": return Connected;
            case "OpenLoopbackTunnel": return new TunnelTestForward();
            case "DisposeAsync": Disposed = true; Connected = false; return ValueTask.CompletedTask;
            default: throw new NotSupportedException(method?.Name);
        }
    }
}
sealed class TunnelTestForward : IServerCenterSshTunnel
{
    public int LocalPort => 51000;
    public string LocalBaseUrl => "http://127.0.0.1:51000";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class TunnelProbeHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Options || request.RequestUri?.Host != "127.0.0.1" || request.RequestUri.Port != 51000)
            throw new Exception("The probe must use the tunnel's transport address and send no credentials.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
    }
}
class TunnelCredentialProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "FindAsync" => Task.FromResult<SshCredentialRecord?>(null),
        _ => throw new NotSupportedException(method?.Name)
    };
}
class TunnelAuthProxy : DispatchProxy
{
    private AuthSessionState _state;
    private EventHandler<AuthSessionStateChangedEventArgs>? _changed;
    public int Logins;
    public bool FailLogin;
    public ServerConnectionIdentity? Identity;
    public void SignOut() { _state = AuthSessionState.Unauthenticated; _changed?.Invoke(this, new(_state)); }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "add_StateChanged": _changed += (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null;
            case "remove_StateChanged": _changed -= (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null;
            case "get_State": return _state;
            case "LoginAsync":
                Logins++;
                if (FailLogin) throw new HttpRequestException("Test failure");
                Identity = (ServerConnectionIdentity)args![0]!;
                var request = (LoginRequest)args[1]!;
                if (request.Identifier != "server-user" || request.Password != "server-password") throw new Exception("Server credentials were mixed with SSH credentials.");
                _state = AuthSessionState.Authenticated;
                return Task.FromResult<LoginResponse>(null!);
            default: throw new NotSupportedException(method?.Name);
        }
    }
}
