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
