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
    internal static int Run()
    {
        using var temporary = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { ApplicationName = typeof(MihomoRegistrationChecks).Assembly.GetName().Name, ContentRootPath = temporary.Path, EnvironmentName = "Testing" });
        builder.AddProxyServices();
        builder.AddDeploymentServices();
        var database = System.IO.Path.Combine(temporary.Path, "registration.db");
        builder.Services.Configure<RelaxKonOS.Server.Storage.StorageOptions>(options => options.DatabasePath = database);
        builder.AddServerStorage(new RelaxKonOS.Server.Storage.StorageOptions { DatabasePath = database }, "sqlite");
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IServerModeResolver>(new FixedMode());
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new NoHelper());
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IOutboundProxyHttpClientFactory>(new NoDownloads());
        using var services = builder.Services.BuildServiceProvider();
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
        Check(scope.ServiceProvider.GetRequiredService<IProxyConfigurationTransactionService>() is ProxyConfigurationTransactionService
            && services.GetRequiredService<IProxySettingsService>() is MihomoSettingsService,
            "Default profile and settings writers resolve the shared publication authority");
        // Only the Mihomo ledger entry is factory-registered; instantiate exactly that one so the
        // fixture never has to construct unrelated package installers.
        var descriptor = builder.Services.Single(x => x.ServiceType == typeof(RelaxKonOS.Server.Installations.IInstallationService)
            && x.ImplementationFactory is not null);
        var ledger = (RelaxKonOS.Server.Installations.IInstallationService)descriptor.ImplementationFactory!(services)!;
        Check(ledger.Id == RelaxKonOS.Protocol.Installations.InstallationServiceId.Mihomo
            && ReferenceEquals(ledger, services.GetRequiredService<RelaxKonOS.Server.Installations.MihomoRuntimeInstallationService>()),
            "Mihomo installs only through the managed catalog adapter, registered once");
        Check(builder.Services.Any(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MihomoPublicationRecoveryService)),
            "Default startup registers pending publication recovery");
        return count;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mihomo-registration-" + Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
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
