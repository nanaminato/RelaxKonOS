using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy;

/// <summary>Adds an async disposable to a real Proxy execution scope in the private Server fixture.</summary>
internal sealed class MihomoTunCleanupBarrier(string root, string prefix) : IAsyncDisposable
{
    internal static void Register(string root) => Register(root, typeof(MihomoManagedTunOperations), "full-tun-cleanup");

    internal static void RegisterOverview(string root) => Register(root, typeof(IProxyPlatformService), "full-overview-cleanup");

    internal static void RegisterSettings(string root) => Register(root, typeof(IProxySettingsWriter), "full-settings-cleanup");

    private static void Register(string root, Type serviceType, string prefix)
    {
        DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry is { Key: "HostBuilding", Value: IHostBuilder host })
                        host.ConfigureServices(services => AddBarrier(services, root, serviceType, prefix));
                }));
        }));
    }

    private static void AddBarrier(IServiceCollection services, string root, Type serviceType, string prefix)
    {
        // The private Server also builds the privilege broker as a separate host.
        var execution = services.SingleOrDefault(service => service.ServiceType == serviceType);
        if (execution is null) return;
        services.Remove(execution);
        services.AddScoped(_ => new MihomoTunCleanupBarrier(root, prefix));
        services.Add(ServiceDescriptor.Describe(execution.ServiceType, provider =>
        {
            provider.GetRequiredService<MihomoTunCleanupBarrier>();
            return ActivatorUtilities.CreateInstance(provider, execution.ImplementationType!);
        }, execution.Lifetime));
    }

    public async ValueTask DisposeAsync()
    {
        if (!File.Exists(Path.Combine(root, prefix + "-arm"))) return;
        await File.WriteAllTextAsync(Path.Combine(root, prefix + "-held"), "disposing");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(root, prefix + "-release")))
            await Task.Delay(25, budget.Token);
        if (File.Exists(Path.Combine(root, prefix + "-fail")))
            throw new IOException("Private Proxy scope cleanup failed at the fixture boundary.");
        await File.WriteAllTextAsync(Path.Combine(root, prefix + "-complete"), "disposed");
    }

    private sealed class Observer<T>(Action<T> receive) : IObserver<T>
    {
        public void OnNext(T value) => receive(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
}
