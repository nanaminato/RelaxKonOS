using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy;

/// <summary>Adds an async disposable to a real Proxy execution scope in the private Server fixture.</summary>
internal sealed class MihomoTunCleanupBarrier(string root, string prefix) : IAsyncDisposable
{
    internal static void Register(string root) => Register(root, typeof(MihomoManagedTunOperations), "full-tun-cleanup");

    internal static void RegisterOverview(string root) => Register(root, typeof(IProxyPlatformService), "full-overview-cleanup");

    internal static void RegisterSettings(string root) => Register(root, typeof(IProxySettingsWriter), "full-settings-cleanup");
    internal static void RegisterSquid(string root) => Register(root, typeof(SquidGatewayService), "full-squid-cleanup");
    internal static void RegisterController(string root) => Register(root, typeof(IMihomoControllerClient), "full-controller-cleanup");
    internal static void RegisterRetry(string root) => Register(root, typeof(MihomoRuntimeRecoveryService), "full-pam-retry-cleanup",
        services => services.AddSingleton<IStartupFilter>(new RetryResponseBarrier(root)));

    private static void Register(string root, Type serviceType, string prefix, Action<IServiceCollection>? configure = null)
    {
        DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry is { Key: "HostBuilding", Value: IHostBuilder host })
                        host.ConfigureServices(services => AddBarrier(services, root, serviceType, prefix, configure));
                }));
        }));
    }

    private static void AddBarrier(IServiceCollection services, string root, Type serviceType, string prefix, Action<IServiceCollection>? configure)
    {
        // The private Server also builds the privilege broker as a separate host.
        var execution = services.SingleOrDefault(service => service.ServiceType == serviceType);
        if (execution is null) return;
        configure?.Invoke(services);
        services.Remove(execution);
        services.AddScoped(_ => new MihomoTunCleanupBarrier(root, prefix));
        services.Add(ServiceDescriptor.Describe(execution.ServiceType, provider =>
        {
            provider.GetRequiredService<MihomoTunCleanupBarrier>();
            return execution.ImplementationFactory is { } factory
                ? factory(provider)
                : ActivatorUtilities.CreateInstance(provider, execution.ImplementationType!);
        }, execution.Lifetime));
    }

    public async ValueTask DisposeAsync()
        => await HoldAsync(root, prefix, "disposing", "disposed");

    private static async Task HoldAsync(string root, string prefix, string held, string complete)
    {
        if (!File.Exists(Path.Combine(root, prefix + "-arm"))) return;
        await File.WriteAllTextAsync(Path.Combine(root, prefix + "-held"), held);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(root, prefix + "-release")))
            await Task.Delay(25, budget.Token);
        if (File.Exists(Path.Combine(root, prefix + "-fail")))
            throw new IOException("Private Proxy scope cleanup failed at the fixture boundary.");
        await File.WriteAllTextAsync(Path.Combine(root, prefix + "-complete"), complete);
    }

    private sealed class RetryResponseBarrier(string root) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, resume) =>
            {
                if (context.Request.Path == RuntimeRoutes.MihomoRecoveryRetry)
                    context.Response.OnStarting(() => HoldAsync(root, "full-pam-retry-response", "sending", "released"));
                return resume(context);
            });
            next(app);
        };
    }

    private sealed class Observer<T>(Action<T> receive) : IObserver<T>
    {
        public void OnNext(T value) => receive(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
}
