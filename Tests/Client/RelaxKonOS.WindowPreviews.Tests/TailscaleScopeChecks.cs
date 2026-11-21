using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Protocol.Workspace;

internal static class TailscaleScopeChecks
{
    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.tailscale");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var session = DispatchProxy.Create<IAuthSession, TailscaleScopeAuthFixture>();
        var auth = (TailscaleScopeAuthFixture)(object)session;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "tailscale-sdk-memory.json"));
        var control = DispatchProxy.Create<IServerTailscale, TailscaleScopeTransport>();
        var login = DispatchProxy.Create<IServerTailscaleLogin, TailscaleScopeTransport>();
        var loginFixture = (TailscaleScopeTransport)(object)login;
        var broker = new Broker();
        using var lifetime = new CancellationTokenSource();
        var scoped = new ServerTailscaleCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            control, login, broker, lifetime.Token);
        IServerTailscaleLogin scopedLogin = scoped;
        await RejectAsync<RelaxKonOSAuthException>(() => Task.FromResult(scoped.CaptureConnection()));

        permissions.Granted.Add(AppPermissions.ServerTailscaleRead);
        var identity = scoped.CaptureConnection();
        var status = await scoped.GetStatusAsync(identity, default);
        if (status.Account is not null || status.TailnetName is not null || !status.PeersRestricted)
            throw new InvalidOperationException("Tailscale read-only scope returned restricted account or tailnet details.");
        await RejectAsync<RelaxKonOSAuthException>(() => scopedLogin.ChallengeAsync(identity, Guid.NewGuid(), default));

        permissions.Granted.Add(AppPermissions.ServerTailscaleManage);
        await scoped.GetControlAsync(identity, default);
        if (broker.Elevations != 0)
            throw new InvalidOperationException("Passive Tailscale refresh must never request native authorization.");
        await scoped.ReadAuthorizedControlAsync(identity, default);
        if (broker.Elevations != 1)
            throw new InvalidOperationException("An explicit Tailscale settings read must use the scoped grant.");
        var operation = Guid.NewGuid();
        var challenge = await scopedLogin.ChallengeAsync(identity, operation, default);
        if (challenge.OperationId != operation || loginFixture.ChallengeReads != 1 || broker.Elevations != 2
            || !await scoped.RequestPeerAccessAsync(identity, default) || broker.PeerGrants != 1)
            throw new InvalidOperationException("The Tailscale capability did not restrict native authorization to the intended scope.");

        broker.BeforeOperation = () =>
        {
            auth.ServiceId = "https://replacement.invalid";
            auth.NotifyStateChanged();
        };
        await RejectAsync<OperationCanceledException>(() => scopedLogin.ChallengeAsync(identity, operation, default));
        if (loginFixture.ChallengeReads != 1)
            throw new InvalidOperationException("A host switch during native authorization reached the old login transport.");
        Console.WriteLine("Tailscale scoped reads, peer authorization, challenge access, and grant-time host switching passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Broker : IHostElevationBroker
    {
        public int Elevations { get; private set; }
        public int PeerGrants { get; private set; }
        public Action? BeforeOperation { get; set; }

        public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            CheckScope(capability, target);
            Elevations++;
            BeforeOperation?.Invoke();
            return operation();
        }

        public Task<bool> RequestGrantAsync(HostElevationCapability capability, string target, CancellationToken cancellationToken = default)
        {
            CheckScope(capability, target);
            PeerGrants++;
            return Task.FromResult(true);
        }

        private static void CheckScope(HostElevationCapability capability, string target)
        {
            if (capability != HostElevationCapability.HostNetworkChange || target != TailscaleAccess.Resource)
                throw new InvalidOperationException("Tailscale passed an unrelated grant to the native broker.");
        }
    }
}

public class TailscaleScopeAuthFixture : ProxyServerScopeAuthFixture
{
    private readonly SessionDto _login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);

    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_CurrentSession" ? _login : base.Invoke(method, args);
}

public class TailscaleScopeTransport : DispatchProxy
{
    public int ChallengeReads { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IServerTailscale.GetStatusAsync) => Task.FromResult(new TailscaleStatusDto("observed",
            TailnetName: "tailnet-fixture", Account: new("account", "Account"))),
        nameof(IServerTailscale.GetControlAsync) => Task.FromResult(new TailscaleControlSnapshot("fixture", true, true, new(true, true), true)),
        nameof(IServerTailscaleLogin.ChallengeAsync) => ReadChallenge((Guid)args![1]!),
        _ => throw new NotSupportedException(method?.Name),
    };

    private Task<TailscaleLoginChallengeResponse> ReadChallenge(Guid id)
    {
        ChallengeReads++;
        return Task.FromResult(new TailscaleLoginChallengeResponse(id, "https://example.invalid/", DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}
