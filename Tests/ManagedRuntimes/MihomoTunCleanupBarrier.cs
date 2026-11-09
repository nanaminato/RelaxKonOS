using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Adds an async disposable to the real TUN execution scope in the private Server fixture.</summary>
internal sealed class MihomoTunCleanupBarrier(string root) : IAsyncDisposable
{
    internal static void Register(string root)
    {
        DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry is { Key: "HostBuilding", Value: IHostBuilder host })
                        host.ConfigureServices(services => AddBarrier(services, root));
                }));
        }));
    }

    private static void AddBarrier(IServiceCollection services, string root)
    {
        // The private Server also builds the privilege broker as a separate host.
        var execution = services.SingleOrDefault(service => service.ServiceType == typeof(MihomoManagedTunOperations));
        if (execution is null) return;
        services.Remove(execution);
        services.AddScoped(_ => new MihomoTunCleanupBarrier(root));
        services.Add(ServiceDescriptor.Describe(execution.ServiceType, provider =>
        {
            provider.GetRequiredService<MihomoTunCleanupBarrier>();
            return ActivatorUtilities.CreateInstance(provider, execution.ImplementationType!);
        }, execution.Lifetime));
    }

    public async ValueTask DisposeAsync()
    {
        if (!File.Exists(Path.Combine(root, "full-tun-cleanup-arm"))) return;
        await File.WriteAllTextAsync(Path.Combine(root, "full-tun-cleanup-held"), "disposing");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(root, "full-tun-cleanup-release")))
            await Task.Delay(25, budget.Token);
        await File.WriteAllTextAsync(Path.Combine(root, "full-tun-cleanup-complete"), "disposed");
    }

    private sealed class Observer<T>(Action<T> receive) : IObserver<T>
    {
        public void OnNext(T value) => receive(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
}
