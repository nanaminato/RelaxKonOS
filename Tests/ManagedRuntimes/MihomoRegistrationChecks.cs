using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Startup;

internal static class MihomoRegistrationChecks
{
    internal static async Task<int> RunAsync()
    {
        using var temporary = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { ApplicationName = typeof(MihomoRegistrationChecks).Assembly.GetName().Name, ContentRootPath = temporary.Path, EnvironmentName = "Testing" });
        // Host backbone first (runtime, publication ledger, settings), then the proxy module's own
        // surface: the profile/subscription/configuration services moved there, so this fixture has to
        // register the same pair the Host does instead of the old single AddProxyServices call.
        builder.AddProxyRuntimeServices();
        ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId }).ConfigureServices(builder,
            ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite"));
        var proxyHostedFactories = builder.Services.Where(item => item.ServiceType == typeof(IHostedService)
            && item.ImplementationFactory is not null).ToArray();
        builder.AddDeploymentServices();
        var database = System.IO.Path.Combine(temporary.Path, "registration.db");
        builder.Services.Configure<RelaxKonOS.Server.Storage.StorageOptions>(options => options.DatabasePath = database);
        builder.AddServerStorage(new RelaxKonOS.Server.Storage.StorageOptions { DatabasePath = database }, "sqlite");
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IServerModeResolver>(new FixedMode());
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new NoHelper());
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory>(new NoDownloads());
        // Mirrors HostServiceRegistration: the resolver is looked up per operation, and that deferral is
        // exactly what keeps the runtime service, its publication coordinator and the proxy settings
        // service out of a construction cycle. Registering a stub here would hide that.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyResolver, RelaxKonOS.Server.Docker.DockerProxyResolver>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider>(container =>
            new RelaxKonOS.Server.ManagedRuntimes.RuntimeDownloadProxyProvider(
                () => container.GetRequiredService<RelaxKonOS.Server.Docker.IDockerProxyResolver>()));
        await using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var store = services.GetRequiredService<MihomoPublicationStore>();
        Check(ReferenceEquals(store, services.GetRequiredService<MihomoPublicationStore>()), "Default publication authority and gate are singleton");
        Check(services.GetRequiredService<MihomoRuntimeRecoveryService>() is not null
            && services.GetRequiredService<MihomoRuntimeService>() is not null
            && services.GetRequiredService<MihomoConfigurationPublicationService>() is not null
            && services.GetRequiredService<MihomoInstallationPublicationService>() is not null
            && services.GetRequiredService<MihomoInstallationBootstrapService>() is not null
            && services.GetRequiredService<MihomoRemovalPublicationService>() is not null,
            "Default runtime and publication coordinators resolve with the current contracts");
        Check(new[] { typeof(MihomoConfigurationPublicationService), typeof(MihomoInstallationPublicationService), typeof(MihomoRemovalPublicationService), typeof(MihomoRuntimeRecoveryService) }
            .All(type => builder.Services.Single(x => x.ServiceType == type).Lifetime == ServiceLifetime.Transient),
            "Publication coordinators and manual recovery have per-resolution instances and retain the singleton publication authority");
        Check(scope.ServiceProvider.GetRequiredService<IProxyConfigurationTransactionService>() is ProxyConfigurationTransactionService
            && services.GetRequiredService<IProxySettingsReader>() is ProxySettingsReader,
            "Default profile transactions and lightweight settings reader resolve their current contracts");
        Check(services.GetRequiredService<IProxySystemProxyReconciler>() is MihomoSystemProxyReconciler
            && services.GetRequiredService<IProxyTunRuntimeController>() is MihomoTunRuntimeController
            && !ReferenceEquals(services.GetRequiredService<IProxySettingsWriter>(), services.GetRequiredService<IProxySettingsWriter>()),
            "Guard and TUN have narrow executors while settings writers are created per invocation");
        Check(builder.Services.Single(x => x.ServiceType == typeof(MihomoSettingsService)).Lifetime == ServiceLifetime.Transient
            && builder.Services.Single(x => x.ServiceType == typeof(IProxySettingsWriter)).Lifetime == ServiceLifetime.Transient,
            "The root container does not retain a singleton settings writer");
        Check(builder.Services.Count(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(SystemProxyGuardHostedService)) == 1,
            "Proxy registration keeps exactly one Core system proxy guard");
        Check(services.GetRequiredService<ProxySubscriptionOperations>() is not null
            && proxyHostedFactories.Count(item => ReferenceEquals(item.ImplementationFactory!(services),
                services.GetRequiredService<ProxyOperationStore>())) == 1,
            "The subscription queue uses the same singleton operation ledger registered once for shutdown drain");
        Check(builder.Services.Single(item => item.ServiceType == typeof(RelaxKonOS.Server.ProxyServer.ProxyServerService)).Lifetime
                == ServiceLifetime.Transient
            && builder.Services.Single(item => item.ServiceType == typeof(RelaxKonOS.Server.ProxyServer.IProxyServerExpiryExecutor)).Lifetime
                == ServiceLifetime.Transient,
            "Inbound management and expiry executors are not rooted as singleton services");
        Check(builder.Services.Count(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(RelaxKonOS.Server.ProxyServer.ProxyServerExpiryService)) == 1,
            "Proxy registration keeps exactly one expiry scheduler");
        // Only the Mihomo ledger entry is factory-registered; instantiate exactly that one so the
        // fixture never has to construct unrelated package installers.
        var descriptor = builder.Services.Single(x => x.ServiceType == typeof(RelaxKonOS.Server.Installations.IInstallationService)
            && x.ImplementationFactory is not null);
        var ledger = (RelaxKonOS.Server.Installations.IInstallationService)descriptor.ImplementationFactory!(services)!;
        Check(ledger.Id == RelaxKonOS.Protocol.Installations.InstallationServiceId.Mihomo
            && ReferenceEquals(ledger, services.GetRequiredService<RelaxKonOS.Server.Installations.MihomoRuntimeInstallationService>()),
            "Mihomo installs only through the managed catalog adapter, registered once");
        Check(builder.Services.Any(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MihomoPublicationRecoveryScheduler)),
            "Default startup registers pending publication recovery");
        // Resolving the runtime service while the real Docker proxy resolver is registered is the assertion:
        // an eager resolver dependency closes the cycle this fixture used to hide with a stub.
        Check(services.GetRequiredService<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeService>() is not null,
            "Runtime service resolves alongside the real download-proxy resolver without a construction cycle");
        builder.Services.AddSingleton<MihomoSettingsService>(_ => throw new Exception("Safety executor constructed the settings writer"));
        using (var tunServices = builder.Services.BuildServiceProvider())
        {
            using var recovery = ActivatorUtilities.CreateInstance<ProxyRecoveryHostedService>(tunServices);
            Check(tunServices.GetRequiredService<IProxyTunSafetyService>() is ProxyTunSafetyService
                && tunServices.GetRequiredService<IProxyTunRuntimeController>() is MihomoTunRuntimeController,
                "Production TUN startup recovery constructs without the settings writer");
        }
        builder.Services.AddSingleton<IMihomoControllerClient>(_ => throw new Exception("Guard constructed a controller client"));
        using var guardServices = builder.Services.BuildServiceProvider();
        using var guard = ActivatorUtilities.CreateInstance<SystemProxyGuardHostedService>(guardServices);
        Check(guardServices.GetRequiredService<IProxySystemProxyReconciler>() is MihomoSystemProxyReconciler,
            "Production guard constructs without the settings writer, TUN controller or direct controller client");
        return count;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mihomo-registration-" + Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        // A pooled SQLite connection can keep the scratch file open briefly; a scratch directory is not
        // worth failing the check over, so a busy file is left to the OS instead of aborting the run.
        public void Dispose()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { Directory.Delete(Path, true); return; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }
    private sealed class NoHelper : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default) =>
            throw new Exception("Resolving publication registrations invoked Helper");
    }
    private sealed class NoDownloads : RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory
    {
        public Task<HttpClient> CreateAsync(RelaxKonOS.Server.Docker.OutboundProxyTarget target, TimeSpan timeout, CancellationToken ct = default) =>
            throw new Exception("Resolving publication registrations started a download");
    }
    private sealed class FixedMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
