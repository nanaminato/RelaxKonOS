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
            var credentials = new SshCredentialStore(directory);
            var vm = new LoginViewModel(auth, new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient(new TunnelProbeHandler()), new ServerCertificateTrust(directory)),
                new SshDesktopSession(null!), targets, keys,
                credentials, resolver, new LoginTunnelStore(directory))
            {
                UseLoginTunnel = true, TunnelHost = "example.com", TunnelUserName = "ssh-user",
                TunnelSecret = "ssh-password", ServerUrl = "http://127.0.0.1:5000",
                Identifier = "server-user", Password = "server-password", RememberServer = false, RememberSshCredential = false,
                ConfirmTunnelHostKeyAsync = (_, _) => Task.FromResult(false)
            };
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert(authState.Logins == 0 && factory.Transports.All(t => t.Disposed), "Rejecting a host key sends no Server credentials and releases SSH.");
            Assert((await credentials.LoadAsync()).Count == 0, "Rejecting a host key does not persist SSH credentials.");
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

            authState.FailLogin = false;
            vm.RememberSshCredential = true;
            vm.TunnelSecret = "persisted-ssh-password";
            await vm.TestTunnelCommand.ExecuteAsync(null);
            var reloadedStore = new SshCredentialStore(directory);
            var saved = await reloadedStore.FindAsync(ServerCenterSshEndpoint.Create("example.com", 22, "ssh-user"));
            Assert(!vm.HasError && saved?.Secret == "persisted-ssh-password" && vm.TunnelCredentialStatus.Length > 0,
                "Tunnel testing writes SSH credentials to platform storage and shows a persistent result.");
            var fresh = new LoginViewModel(auth, new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient(new TunnelProbeHandler()), new ServerCertificateTrust(directory)),
                new SshDesktopSession(null!), targets, keys, reloadedStore, resolver, new LoginTunnelStore(directory))
            {
                UseLoginTunnel = true, TunnelHost = "example.com", TunnelUserName = "ssh-user",
                ServerUrl = "http://127.0.0.1:5000", Identifier = "server-user", Password = "server-password"
            };
            await fresh.RefreshTunnelCredentialStatusAsync();
            Assert(fresh.RememberSshCredential && fresh.TunnelCredentialStatus.Length > 0 && fresh.TunnelSecret.Length == 0,
                "A fresh login detects saved SSH credentials without exposing the secret in the input.");
            await fresh.TestTunnelCommand.ExecuteAsync(null);
            Assert(!fresh.HasError && factory.Transports.Last().Credential is ServerCenterSshCredential.Password { Secret: "persisted-ssh-password" },
                "A fresh login uses securely stored SSH credentials with an empty input.");
            fresh.UseServerCredentialsForTunnel = true;
            fresh.RememberSshCredential = false;
            fresh.ConfirmTunnelHostKeyAsync = (_, _) => Task.FromResult(true);
            await fresh.ConnectCommand.ExecuteAsync(null);
            Assert(!fresh.HasError && factory.Transports.Last().UserName == "server-user" &&
                factory.Transports.Last().Credential is ServerCenterSshCredential.Password { Secret: "server-password" },
                "Explicit reuse sends the current Server username and password to SSH.");
            authState.SignOut();
            fresh.Password = "";
            var transportsBeforeMissingPassword = factory.Transports.Count;
            await fresh.TestTunnelCommand.ExecuteAsync(null);
            Assert(fresh.HasError && factory.Transports.Count == transportsBeforeMissingPassword,
                "Reuse with a missing Server password does not silently fall back to another SSH credential.");
            fresh.Password = "server-password";
            fresh.UseServerCredentialsForTunnel = false;
            fresh.TunnelSecret = "separate-password";
            fresh.TunnelUserName = "ssh-user";
            fresh.RememberSshCredential = false;
            await fresh.TestTunnelCommand.ExecuteAsync(null);
            Assert(!fresh.HasError && factory.Transports.Last().UserName == "ssh-user" &&
                factory.Transports.Last().Credential is ServerCenterSshCredential.Password { Secret: "separate-password" },
                "Turning reuse off restores independent SSH authentication.");

            var unavailable = new LoginViewModel(auth, new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient(new TunnelProbeHandler()), new ServerCertificateTrust(directory)),
                new SshDesktopSession(null!), targets, keys, DispatchProxy.Create<ISshCredentialStore, TunnelCredentialProxy>(),
                resolver, new LoginTunnelStore(directory))
            {
                UseLoginTunnel = true, TunnelHost = "example.com", TunnelUserName = "ssh-user", TunnelSecret = "ssh-password",
                ServerUrl = "http://127.0.0.1:5000", RememberSshCredential = true
            };
            await unavailable.TestTunnelCommand.ExecuteAsync(null);
            Assert(!unavailable.HasError && unavailable.TunnelCredentialStatus == unavailableStatus(),
                "A save failure remains visible after the successful Server probe.");
            string unavailableStatus() => new LoginLocalizationService(new LocalLanguageStore()).Get("login.tunnel.not_saved", "Connected, but SSH credentials could not be saved securely.");

            var probeFailure = new LoginViewModel(auth, new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient(new TunnelProbeHandler(unreachable: true)), new ServerCertificateTrust(directory)),
                new SshDesktopSession(null!), targets, keys, reloadedStore, resolver, new LoginTunnelStore(directory))
            {
                UseLoginTunnel = true, TunnelHost = "example.com", TunnelUserName = "ssh-user", TunnelSecret = "saved-before-probe",
                ServerUrl = "http://127.0.0.1:5000", RememberSshCredential = true
            };
            await probeFailure.TestTunnelCommand.ExecuteAsync(null);
            Assert(probeFailure.HasError && (await new SshCredentialStore(directory).FindAsync(
                ServerCenterSshEndpoint.Create("example.com", 22, "ssh-user")))?.Secret == "saved-before-probe",
                "Successful SSH authentication is saved even when the remote Server is unreachable.");
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
    public string? UserName;
    public ServerCenterSshCredential? Credential;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "ConnectAsync":
                var endpoint = (ServerCenterSshEndpoint)args![0]!;
                UserName = endpoint.UserName;
                Credential = (ServerCenterSshCredential)args[1]!;
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
sealed class TunnelProbeHandler(bool unreachable = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Options || request.RequestUri?.Host != "127.0.0.1" || request.RequestUri.Port != 51000)
            throw new Exception("The probe must use the tunnel's transport address and send no credentials.");
        if (unreachable) throw new HttpRequestException("Remote Server is unavailable.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
    }
}
class TunnelCredentialProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "FindAsync" => Task.FromResult<SshCredentialRecord?>(null),
        "SaveAsync" => Task.FromResult(SshCredentialSaveResult.WriteFailed),
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
