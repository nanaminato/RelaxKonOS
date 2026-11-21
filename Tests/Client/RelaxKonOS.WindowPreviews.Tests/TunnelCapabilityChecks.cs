using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Tunnels;

internal static class TunnelCapabilityChecks
{
    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.tunnels");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var session = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var auth = (EventAlertsAuthFixture)(object)session;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "tunnel-sdk-memory.json"));
        var client = DispatchProxy.Create<IServerTunnels, TunnelCapabilityClient>();
        var calls = (TunnelCapabilityClient)(object)client;
        using var lifetime = new CancellationTokenSource();
        IServerTunnels tunnels = new ServerTunnelsCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            client, lifetime.Token);
        var profile = Guid.NewGuid();

        await RejectAsync<UnauthorizedAccessException>(() => tunnels.ListProfilesAsync());
        permissions.Granted.Add(AppPermissions.ServerTunnelsRead);
        await tunnels.ListProfilesAsync();
        await RejectAsync<UnauthorizedAccessException>(() => tunnels.GetProfileAsync(profile));
        await RejectAsync<UnauthorizedAccessException>(() => tunnels.GetManagedFrpsForEditingAsync());
        await RejectAsync<UnauthorizedAccessException>(() => tunnels.DeleteProfileAsync(profile));
        if (calls.EditorReads != 0) throw new InvalidOperationException("Tunnel edit data reached a read-only application.");

        permissions.Granted.Add(AppPermissions.ServerTunnelsManage);
        await tunnels.GetProfileAsync(profile);
        if (calls.EditorReads != 1) throw new InvalidOperationException("Tunnel edit data was not delegated.");

        var pending = tunnels.GetRuntimeAsync();
        auth.ServiceId = "http://replacement.invalid";
        auth.NotifyStateChanged();
        await RejectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await RejectAsync<InvalidOperationException>(() => tunnels.ListProfilesAsync());
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => tunnels.ListProfilesAsync());
        Console.WriteLine("Tunnel SDK read, edit and lifecycle checks passed.");

        static async Task RejectAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }
}

public class TunnelCapabilityClient : DispatchProxy
{
    public int EditorReads { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case nameof(IServerTunnels.ListProfilesAsync):
                return Task.FromResult<IReadOnlyList<TunnelServerProfileDto>>([]);
            case nameof(IServerTunnels.GetProfileAsync):
                EditorReads++;
                return Task.FromResult<TunnelServerProfileDto?>(null);
            case nameof(IServerTunnels.GetRuntimeAsync):
                return WaitAsync((CancellationToken)args![0]!);
            default:
                throw new InvalidOperationException($"Unexpected tunnel call: {method?.Name}.");
        }
    }

    private static async Task<TunnelRuntimeDto> WaitAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("The tunnel request must be cancelled.");
    }
}
