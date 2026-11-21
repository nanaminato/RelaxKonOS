using RelaxKonOS.App.ProxyManager;
using System.Net;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Workspace;

internal static class ProxyScopeChecks
{
    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.proxy");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var session = DispatchProxy.Create<IAuthSession, ProxyServerScopeAuthFixture>();
        var auth = (ProxyServerScopeAuthFixture)(object)session;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "proxy-sdk-memory.json"));
        var transport = DispatchProxy.Create<IServerProxy, ProxyScopeTransport>();
        var fixture = (ProxyScopeTransport)(object)transport;
        using var lifetime = new CancellationTokenSource();
        var proxy = new ServerProxyCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            () => transport, lifetime.Token);
        var subscription = Guid.NewGuid();

        await DeniedAsync(() => proxy.GetTrafficAsync());
        permissions.Granted.Add(AppPermissions.ServerProxyRead);
        await DeniedAsync(() => proxy.GetSubscriptionContentAsync(subscription));
        await DeniedAsync(() => proxy.EnableTunAsync(Guid.NewGuid()));
        permissions.Granted.Add(AppPermissions.ServerProxyManage);
        await proxy.GetSubscriptionContentAsync(subscription);
        await DeniedAsync(() => proxy.DisableTunAsync());
        permissions.Granted.Add(AppPermissions.ServerProxyTunManage);
        permissions.Granted.Remove(AppPermissions.ServerProxyManage);
        var queued = await proxy.DisableTunAsync();
        auth.Role = DeviceRole.Observer;
        await DeniedAsync(() => proxy.DisableTunAsync());
        if (await proxy.GetOperationAsync(queued.OperationId) is null)
            throw new InvalidOperationException("Proxy read access must allow its own operation receipt.");
        fixture.OperationKind = "squid.configuration.apply";
        if (await proxy.GetOperationAsync(queued.OperationId) is not null)
            throw new InvalidOperationException("The Proxy capability must hide operations owned by the Squid workspace.");

        var model = new ProxyManagerViewModel(proxy, (key, _) => key, true, true) { RuntimeSubscriptionText = "fixture" };
        var traffic = model.RefreshTrafficAsync(default);
        model.InvalidateSession();
        await traffic.WaitAsync(TimeSpan.FromSeconds(5));
        if (model.HasManagePermission || model.HasTunManagePermission || model.RuntimeSubscriptionText.Length != 0
            || model.StatusText.Resolve() != "proxy.problem.session_changed")
            throw new InvalidOperationException("An inactive Proxy window must cancel sampling and forget subscription content.");
        var pending = proxy.GetTrafficAsync();
        auth.ServiceId = "https://replacement.invalid";
        auth.NotifyStateChanged();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("The old request was not cancelled."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Proxy SDK read, management, TUN, operation scope, and session cancellation passed.");
    }

    private static async Task DeniedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (ProxyRequestException exception) when (exception.StatusCode == HttpStatusCode.Forbidden) { return; }
        throw new InvalidOperationException("The scoped Proxy request was not denied.");
    }
}

public class ProxyScopeTransport : DispatchProxy
{
    public string OperationKind { get; set; } = "tun.disable";

    protected override object Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IServerProxy.GetSubscriptionContentAsync) =>
            Task.FromResult(new ProxySubscriptionContentDto((Guid)args![0]!, "fixture", DateTimeOffset.UtcNow)),
        nameof(IServerProxy.DisableTunAsync) => Task.FromResult(new ProxyOperationAcceptedDto(Guid.NewGuid())),
        nameof(IServerProxy.GetOperationAsync) => Task.FromResult<ProxyOperationDto?>(
            new((Guid)args![0]!, OperationKind, ProxyOperationState.Succeeded, "", "", null, null)),
        nameof(IServerProxy.GetTrafficAsync) => WaitAsync((CancellationToken)args![0]!),
        _ => throw new NotSupportedException(method?.Name),
    };

    private static async Task<ProxyTrafficDto> WaitAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The old request must be cancelled.");
    }
}
