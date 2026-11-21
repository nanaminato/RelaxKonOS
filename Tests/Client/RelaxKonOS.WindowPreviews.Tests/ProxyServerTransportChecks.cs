using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Core.Applications;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Workspace;

internal static class ProxyServerTransportChecks
{
    public static async Task RunAsync()
    {
        var session = DispatchProxy.Create<IAuthSession, ProxyServerTransportAuthFixture>();
        var auth = (ProxyServerTransportAuthFixture)(object)session;
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new RemoteProxyServerClient(http, session);
        var id = Guid.NewGuid();
        await client.SavePrincipalAsync(new(null, "New", null), "create", default);
        await client.SavePrincipalAsync(new(id, "Updated", null), "update", default);
        await client.RunLifecycleAsync(ProxyServerAction.Restart, "restart", default);
        if (!handler.Requests.SequenceEqual(new[]
        {
            (HttpMethod.Post, ProxyServerRoutes.Principals, "create"),
            (HttpMethod.Put, ProxyServerRoutes.Principals + "/" + id.ToString("D"), "update"),
            (HttpMethod.Post, ProxyServerRoutes.RuntimeActions + "/restart", "restart"),
        }))
            throw new InvalidOperationException("Typed Proxy Server operations must preserve verb, route, and idempotency keys.");

        await RejectAsync<ArgumentOutOfRangeException>(() => client.RunLifecycleAsync(ProxyServerAction.Install, "install", default));
        auth.Endpoint = "https://replacement.invalid/";
        await RejectAsync<InvalidOperationException>(() => client.DeletePrincipalAsync(id, "delete", default));
        if (handler.Requests.Count != 3)
            throw new InvalidOperationException("An unsupported action or replaced endpoint reached the transport.");
        Console.WriteLine("Proxy Server typed routes, idempotency, and endpoint binding passed.");
    }

    public static async Task VerifyScopeAsync()
    {
        var id = new AppId("relaxkonos.proxy-server");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var session = DispatchProxy.Create<IAuthSession, ProxyServerScopeAuthFixture>();
        var auth = (ProxyServerScopeAuthFixture)(object)session;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "proxy-server-sdk-memory.json"));
        var transport = DispatchProxy.Create<IServerProxyServer, ProxyServerScopeTransport>();
        using var lifetime = new CancellationTokenSource();
        var scoped = new ServerProxyServerCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            () => transport, lifetime.Token);
        var credential = Guid.NewGuid();

        permissions.Granted.Add(AppPermissions.ServerProxyRead);
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ReadAsync(default));
        permissions.Granted.Add(AppPermissions.ServerProxyServerRead);
        await scoped.ReadAsync(default);
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ApplyAsync("key", default));
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ExportAsync(credential, "uri", default));
        permissions.Granted.Add(AppPermissions.ServerProxyServerManage);
        await scoped.ApplyAsync("key", default);
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ExportAsync(credential, "uri", default));
        permissions.Granted.Add(AppPermissions.ServerProxyServerExport);
        permissions.Granted.Remove(AppPermissions.ServerProxyServerManage);
        await scoped.ExportAsync(credential, "uri", default);
        auth.Role = DeviceRole.Observer;
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ExportAsync(credential, "uri", default));

        var pending = scoped.StatusAsync(default);
        auth.ServiceId = "https://replacement.invalid";
        auth.NotifyStateChanged();
        await RejectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await RejectAsync<InvalidOperationException>(() => scoped.ReadAsync(default));
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => scoped.ReadAsync(default));
        Console.WriteLine("Proxy Server SDK read, management, export, and session scope passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Key)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "fixture.invalid" || request.Headers.Authorization?.Parameter != "fixture-access")
                throw new InvalidOperationException("Proxy Server must use the captured server and its authenticated token.");
            Requests.Add((request.Method, request.RequestUri.AbsolutePath, request.Headers.GetValues("Idempotency-Key").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new ProxyOperationAcceptedDto(Guid.NewGuid()), options: RelaxKonOSJsonOptions.Default),
            });
        }
    }
}

public class ProxyServerTransportAuthFixture : DispatchProxy
{
    private readonly SessionDto _login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);

    public string Endpoint { get; set; } = "https://fixture.invalid/";

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_CurrentSession" => _login,
        "get_State" => AuthSessionState.Authenticated,
        "get_EffectiveBaseUrl" => Endpoint,
        "GetAccessTokenAsync" => Task.FromResult("fixture-access"),
        _ => throw new NotSupportedException(method?.Name),
    };
}

public class ProxyServerScopeAuthFixture : EventAlertsAuthFixture
{
    public DeviceRole Role { get; set; } = DeviceRole.Controller;

    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_AssignedRole" ? Role : base.Invoke(method, args);
}

public class ProxyServerScopeTransport : DispatchProxy
{
    protected override object Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IServerProxyServer.ReadAsync) => Task.FromResult<ProxyServerSnapshot>(null!),
        nameof(IServerProxyServer.ApplyAsync) => Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid())),
        nameof(IServerProxyServer.ExportAsync) => Task.FromResult(new ProxyServerExport("uri", "fixture")),
        nameof(IServerProxyServer.StatusAsync) => WaitForCancellationAsync((CancellationToken)args![0]!),
        _ => throw new NotSupportedException(method?.Name),
    };

    private static async Task<ProxyServerStatus> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The old request must be cancelled.");
    }
}
