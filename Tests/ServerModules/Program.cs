using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;
using System.Text.Encodings.Web;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception(name);
}

static IConfiguration Config(params (string Key, string? Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => item.Value)).Build();

var empty = new ConfigurationBuilder().Build();
var defaults = ServerModules.Create(empty);

// The default set must reproduce a Server that predates the registry: every official module on.
Check(defaults.IsEnabled(ServerModules.TerminalId), "default enables terminal");
Check(defaults.IsEnabled(ServerModules.FirewallId), "default enables firewall");
Check(defaults.IsEnabled(ServerModules.SystemPerformanceId), "default enables system performance");
Check(defaults.IsEnabled(ServerModules.GitId), "default enables git");
Check(defaults.IsEnabled(ServerModules.DockerId), "default enables docker");
Check(defaults.IsEnabled(ServerModules.CertificatesId), "default enables certificates");
Check(defaults.IsEnabled(ServerModules.WebServerId), "default enables web server");
Check(defaults.IsEnabled(ServerModules.ProxyId), "default enables proxy");
Check(ServerModules.ConfigurationName(ServerModules.SystemPerformanceId) == "SystemPerformance",
    "configuration name is derived from the module ID so the two cannot drift");
Check(new RelaxKonOS.Client.Apps.Terminal.TerminalApp().Manifest.Server.RequiredCapabilities.Contains(ServerCapabilities.Terminal),
    "desktop terminal requires server terminal capability");
Check(new RelaxKonOS.Client.Apps.TaskManager.TaskManagerApp().Manifest.Server.RequiredCapabilities.Contains(ServerCapabilities.Metrics)
    && new RelaxKonOS.Client.Apps.TaskManager.TaskManagerApp().Manifest.Server.RequiredCapabilities.Contains(ServerCapabilities.Processes),
    "desktop task manager requires server metrics and processes capabilities");
Check(new RelaxKonOS.Client.Apps.Git.GitClientApp().Manifest.Server.RequiredCapabilities.Contains(ServerCapabilities.Git),
    "desktop project editor requires server git capability");

// Configuration errors must stop startup instead of silently disabling a module.
Reject(() => ServerModules.Create(Config(("Modules:Terminal:Enabled", "perhaps"))), "invalid boolean rejected");
Reject(() => ServerModules.Create(Config(("Modules:Termnal:Enabled", "false"))), "unknown module rejected");
Reject(() => ServerModules.Create(Config(("Modules:Firewal:Enabled", "false"))), "unknown module rejected for every module name");
Reject(() => ServerModules.Create(Config(("Modules:Terminal", "false"))), "malformed module section rejected");
Reject(() => ServerModules.Create(Config(("Modules:Terminal:Enable", "false"))), "unknown module setting rejected");
Reject(() => ServerModules.Create(Config(("Modules:Terminal:Enabled:Enabled", "false"))), "nested module setting rejected");
Reject(() => ServerModules.Create(Config(("Modules:Terminal:Enabled", "false"), ("Modules:Terminal:Extra", "1"))), "extra module setting rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", [], [ServerCapabilities.Files]), new FakeModule("a", [], [])],
    new HashSet<string> { "a" }), "duplicate module rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", ["b"], []), new FakeModule("b", ["a"], [])],
    new HashSet<string> { "a", "b" }), "dependency cycle rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", ["b"], [])], new HashSet<string> { "a" }), "missing dependency rejected");
Reject(() => ServerModules.Resolve([new FakeModule("relaxkonos.a-b", [], []), new FakeModule("relaxkonos.a.b", [], [])],
    new HashSet<string>()), "module configuration name collision rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", [], ["server.metric"])], new HashSet<string>()),
    "undeclared capability name rejected");

foreach (var disabled in new[] { ServerModules.TerminalId, ServerModules.FirewallId, ServerModules.SystemPerformanceId,
             ServerModules.GitId, ServerModules.DockerId, ServerModules.WebServerId, ServerModules.ProxyId })
{
    var name = ServerModules.ConfigurationName(disabled);
    var modules = ServerModules.Create(Config(($"Modules:{name}:Enabled", "false")));
    Check(!modules.IsEnabled(disabled), $"explicit false disables {disabled}");
    Check(modules.EnabledIds.Count == 7, $"other modules stay enabled when {disabled} is disabled");
    Check(ServerModules.Create(Config(($"Modules:{name}:Enabled", "true"))).IsEnabled(disabled),
        $"explicit true keeps {disabled}");
}

// The one real hard dependency in the catalog: website publication binds a site to a certificate.
Reject(() => ServerModules.Create(Config(("Modules:Certificates:Enabled", "false"))),
    "disabling a module that another enabled module requires stops startup");
Check(ServerModules.Create(Config(("Modules:Certificates:Enabled", "false"), ("Modules:WebServer:Enabled", "false")))
        .EnabledIds.Count == 6,
    "the dependent module can be disabled together with its dependency");
Check(ServerModules.Create(Config(("Modules:WebServer:Enabled", "false"))).IsEnabled(ServerModules.CertificatesId),
    "disabling the dependent module leaves the certificate module enabled");

Check(ServerModules.Create(Config(("Modules:Firewall:Enabled", "false")))
        .IsEnabled(ServerModules.SystemPerformanceId),
    "disabling one module never cascades into an unrelated module");

// Capability sets follow the snapshot: a disabled module contributes nothing.
Check(defaults.Capabilities.SetEquals([ServerCapabilities.Terminal, ServerCapabilities.Firewall,
        ServerCapabilities.Metrics, ServerCapabilities.Processes, ServerCapabilities.Git, ServerCapabilities.Docker,
        ServerCapabilities.Certificates, ServerCapabilities.WebServer,
        ServerCapabilities.Proxy, ServerCapabilities.GatewayProxy, ServerCapabilities.ProxyServer]),
    "default capability set is exactly the declared module capabilities");

// Web server / website publication is asserted without a host: its publication coordinator is bound
// to the application-deployment domain, and its routes are verified against a real Server process.
{
    static ServerDescriptorDto Describe(ServerModules modules) => ServerDescriptorFactory.Create(
        HostPlatformKind.Windows,
        new ServerModeResolver(new ConfigurationBuilder().Build(), RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper),
        new EventAlertsOptions(), new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions()), modules.Capabilities);

    var advertised = Describe(defaults);
    Check(advertised.Capabilities.Contains(ServerCapabilities.WebServer) && advertised.Host!.Capabilities.WebServer,
        "web server capability is advertised together with the Host flag");
    var withdrawn = Describe(ServerModules.Create(Config(("Modules:WebServer:Enabled", "false"))));
    Check(!withdrawn.Capabilities.Contains(ServerCapabilities.WebServer) && !withdrawn.Host!.Capabilities.WebServer,
        "disabling the web server module withdraws its capability from both descriptions");
    Check(ServerModules.Create(Config(("Modules:WebServer:Enabled", "false"))).IsEnabled(ServerModules.CertificatesId),
        "disabling publication keeps the certificate module it depended on");
}

// The proxy family: one module owns three application capabilities, and it must never register the
// runtime/settings backbone. Task orchestration, the installation ledger and the Docker proxy
// resolver resolve that backbone whether or not this process serves the proxy UI, so a disabled
// module has to leave it exactly as the Host registered it.
{
    static ServerDescriptorDto Describe(ServerModules modules) => ServerDescriptorFactory.Create(
        HostPlatformKind.Windows,
        new ServerModeResolver(new ConfigurationBuilder().Build(), RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper),
        new EventAlertsOptions(), new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions()), modules.Capabilities);

    var advertised = Describe(defaults);
    Check(advertised.Capabilities.Contains(ServerCapabilities.Proxy)
            && advertised.Capabilities.Contains(ServerCapabilities.GatewayProxy)
            && advertised.Capabilities.Contains(ServerCapabilities.ProxyServer)
            && advertised.Host!.Capabilities.Proxy,
        "the proxy module advertises all three proxy applications together");
    var withdrawn = Describe(ServerModules.Create(Config(("Modules:Proxy:Enabled", "false"))));
    Check(!withdrawn.Capabilities.Contains(ServerCapabilities.Proxy)
            && !withdrawn.Capabilities.Contains(ServerCapabilities.GatewayProxy)
            && !withdrawn.Capabilities.Contains(ServerCapabilities.ProxyServer)
            && !withdrawn.Host!.Capabilities.Proxy,
        "disabling the proxy module withdraws every proxy capability from both descriptions");

    foreach (var enabled in new[] { true, false })
    foreach (var (storage, storageBound) in new[] { ("sqlite", true), ("memory", false) })
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modules:Proxy:Enabled"] = enabled.ToString(),
        });
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, storage);
        var modules = ServerModules.Create(builder.Configuration);
        builder.Services.AddDataProtection();
        // The Host registers the backbone first; a module may only add its own surface on top.
        builder.AddProxyRuntimeServices();
        var backbone = new[]
        {
            typeof(RelaxKonOS.Server.Proxy.IProxySettingsService),
            typeof(RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore),
            typeof(RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager),
            typeof(RelaxKonOS.Server.ManagedRuntimes.SingBoxRuntimeService),
            typeof(RelaxKonOS.Server.ProxyServer.ProxyServerStore),
        }.ToDictionary(type => type, type => builder.Services.Count(service => service.ServiceType == type));
        modules.ConfigureServices(builder, context);
        Check(backbone.All(entry => builder.Services.Count(service => service.ServiceType == entry.Key) == entry.Value),
            $"the proxy module never registers the shared runtime backbone enabled={enabled} storage={storage}");
        // The profile/subscription services only exist where their repositories do; a memory-storage
        // host must not gain a service whose repository is missing.
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Proxy.IProxyProfileService))
                == (enabled && storageBound),
            $"storage-bound proxy services follow both the module and the provider enabled={enabled} storage={storage}");
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Proxy.IProxyEngineRegistry)) == enabled,
            $"proxy engine registry registration enabled={enabled}");
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Proxy.IProxyLifecycleService)) == enabled,
            $"proxy lifecycle registration enabled={enabled}");
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.ProxyServer.ProxyServerService)) == enabled,
            $"proxy server operations registration enabled={enabled}");
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.GatewayProxy.SquidGatewayService)) == enabled,
            $"squid gateway registration enabled={enabled}");
        Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Proxy.IProxyTunSafetyService)) == enabled,
            $"tun safety registration enabled={enabled}");
        // Interrupted publications are recovered by Core, so the worker must not follow the UI.
        Check(builder.Services.Any(service => service.ImplementationType == typeof(RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryService)),
            $"publication recovery stays registered enabled={enabled}");
    }
}

foreach (var enabled in new[] { true, false })
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Modules:Terminal:Enabled"] = enabled.ToString(),
        ["Modules:Firewall:Enabled"] = enabled.ToString(),
        ["Modules:SystemPerformance:Enabled"] = enabled.ToString(),
        ["Modules:Git:Enabled"] = enabled.ToString(),
        ["Modules:Docker:Enabled"] = enabled.ToString(),
        ["Modules:Certificates:Enabled"] = enabled.ToString(),
        // The publication coordinator is bound to the application-deployment domain, which this
        // synthetic host does not reconstruct; its routes are covered by the real-process matrix
        // instead. Registration and capability behaviour of that module is asserted below without a host.
        ["Modules:WebServer:Enabled"] = "false",
        // The proxy module is asserted in its own block below with the Host backbone registered.
        // Starting it here would run its background guards against a host this test does not build.
        ["Modules:Proxy:Enabled"] = "false",
    });
    var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite");
    var modules = ServerModules.Create(builder.Configuration);
    builder.Services.AddSignalR();
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, NoAuthentication>("test", _ => { });
    builder.Services.AddAuthorization();
    // Core-owned service the module routes resolve; the module itself must not register it.
    builder.Services.AddSingleton<RelaxKonOS.Server.Privileged.IHostElevationSessionStore, NoElevation>();
    builder.Services.AddDataProtection();
    // Core-owned observability the Docker stack coordinator publishes through.
    builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, NoOperationalEvents>();
    // Core-owned Docker transport: deployments, the runtime installer and managed-runtime downloads
    // need the Engine whether or not the Docker manager is enabled.
    builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineService, NoDockerEngine>();
    // Core-owned web server manager: application deployment and the component center use it, so the
    // WebServer module must not be the thing that registers it.
    builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IWebServerManager, NoWebServerManager>();
    // Core-owned TLS store: the Kestrel boot path and the renewal worker both resolve it.
    builder.Services.AddSingleton(new RelaxKonOS.Server.Certificate.CertificateOptions());
    builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.FileHttp01ChallengeStore>();
    builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateMetadataRepository>();
    builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.CertificateDeploymentRepository>();
    builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.HostOperationJournal>();
    builder.Services.AddSingleton(new RelaxKonOS.Server.Certificate.KestrelCertificateRegistry());
    builder.Services.AddSingleton<RelaxKonOS.Server.WebServer.IHostPrivilegeService, RelaxKonOS.Server.WebServer.HostPrivilegeService>();
    builder.Services.AddSingleton<RelaxKonOS.Server.Certificate.ICertificateStore, NoCertificateStore>();
    var engineTransportBefore = builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerEngineService));
    var proxyResolverBefore = builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerProxyResolver));
    var certificateStoreBefore = builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Certificate.ICertificateStore));
    var webServerManagerBefore = builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.WebServer.IWebServerManager));
    // Mirror the Host ordering: Core registers the unavailable deployment port, then modules may
    // replace it. This is what makes "Git disabled" a reported status instead of a missing service.
    builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.IDeploymentImageBuildSource,
        RelaxKonOS.Server.ApplicationDeployments.UnavailableDeploymentImageBuildSource>();
    modules.ConfigureServices(builder, context);
    Check(builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerEngineService)) == engineTransportBefore
            && builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerProxyResolver)) == proxyResolverBefore,
        $"the docker module never registers the shared engine transport enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerComposeService)) == enabled,
        $"docker compose registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.DockerStackOperationCoordinator)) == enabled,
        $"docker stack coordinator registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Docker.IDockerProxyService)) == enabled,
        $"docker proxy manager registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Certificate.ICertificateManager)) == enabled,
        $"certificate manager registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Certificate.IAcmeService)) == enabled,
        $"acme service registration enabled={enabled}");
    // TLS boot belongs to Core and must survive a disabled certificate manager: the module may not
    // register the certificate store or the web server manager that other domains depend on.
    Check(builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.Certificate.ICertificateStore)) == certificateStoreBefore
            && builder.Services.Count(service => service.ServiceType == typeof(RelaxKonOS.Server.WebServer.IWebServerManager)) == webServerManagerBefore,
        $"certificate and web server modules never register the Core-owned substrate enabled={enabled}");
    Check(!builder.Services.Any(service => service.ImplementationType == typeof(RelaxKonOS.Server.WebsitePublishing.WebsitePublicationCoordinator)),
        $"disabled publication module registers no background loop enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(IPtyFactory)) == enabled, $"PTY registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(TerminalSessionManager)) == enabled, $"session manager enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Firewall.IHostFirewallService)) == enabled,
        $"firewall service registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.SystemPerformance.PerformanceSampler)) == enabled,
        $"performance sampler registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.SystemPerformance.IProcessService)) == enabled,
        $"process service registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ImplementationType == typeof(RelaxKonOS.Server.Hubs.PerformanceBroadcastService)) == enabled,
        $"performance broadcast background service enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Git.IGitRepositoryService)) == enabled,
        $"git repository service registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Git.GitBuildService)) == enabled,
        $"git build ledger registration enabled={enabled}");
    // The deployment domain only knows the port; the Git module replaces the Host's unavailable
    // default. A disabled module must leave the default in place rather than a missing service.
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.ApplicationDeployments.IDeploymentImageBuildSource)),
        $"deployment image build port stays resolvable enabled={enabled}");

    var mode = new ServerModeResolver(builder.Configuration, RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper);
    var host = ServerDescriptorFactory.Create(HostPlatformKind.Windows, mode, new EventAlertsOptions(),
        new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions()), modules.Capabilities);
    Check(host.Capabilities.Contains(ServerCapabilities.Terminal) == enabled && host.Host!.Capabilities.Terminal == enabled,
        $"descriptor capability and host agree for terminal enabled={enabled}");
    Check(host.Capabilities.Contains(ServerCapabilities.Metrics) == enabled && host.Host!.Capabilities.Metrics == enabled
        && host.Capabilities.Contains(ServerCapabilities.Processes) == enabled && host.Host!.Capabilities.Processes == enabled,
        $"descriptor capability and host agree for performance enabled={enabled}");
    // Firewall needs a privileged host; the module cannot grant it, and the mode resolver here reports
    // System Mode so the boolean is purely module-driven.
    Check(host.Capabilities.Contains(ServerCapabilities.Firewall) == (enabled && host.Host!.Capabilities.Firewall),
        $"descriptor capability and host agree for firewall enabled={enabled}");
    Check(host.Capabilities.Contains(ServerCapabilities.Git) == enabled && host.Host!.Capabilities.Git == enabled,
        $"descriptor capability and host agree for git enabled={enabled}");
    // The Docker manager capability follows the module; the engine itself stays available to
    // deployment and installations either way.
    Check(host.Capabilities.Contains(ServerCapabilities.Docker) == (enabled && host.Host!.Capabilities.Docker),
        $"descriptor capability and host agree for docker enabled={enabled}");
    Check(host.Capabilities.Contains(ServerCapabilities.Certificates) == (enabled && host.Host!.Capabilities.Certificates),
        $"descriptor capability and host agree for certificates enabled={enabled}");

    await using var app = builder.Build();
    var imageBuilds = app.Services.GetRequiredService<RelaxKonOS.Server.ApplicationDeployments.IDeploymentImageBuildSource>();
    Check(imageBuilds.GetType() == (enabled
            ? typeof(RelaxKonOS.Server.Git.GitDeploymentImageBuildSource)
            : typeof(RelaxKonOS.Server.ApplicationDeployments.UnavailableDeploymentImageBuildSource)),
        $"deployment git build port uses the module implementation only when enabled={enabled}");
    var unresolved = imageBuilds.Resolve(Guid.NewGuid(), Guid.NewGuid());
    Check(enabled
            ? unresolved.Status == RelaxKonOS.Server.ApplicationDeployments.DeploymentImageBuildStatus.NotFound
            : unresolved.Status == RelaxKonOS.Server.ApplicationDeployments.DeploymentImageBuildStatus.Unavailable,
        $"unknown build never reports a deployable artifact enabled={enabled}");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHostNetworkEndpoints();
    modules.MapEndpoints(app, context);
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    static async Task<HttpStatusCode> PostAsync(HttpClient client, string path) =>
        (await client.PostAsync(path, null)).StatusCode;
    static async Task<HttpStatusCode> GetAsync(HttpClient client, string path) =>
        (await client.GetAsync(path)).StatusCode;

    Check(await PostAsync(client, "/hubs/terminals/negotiate?negotiateVersion=1") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real terminal hub route and authorization enabled={enabled}");
    Check(await GetAsync(client, $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/firewall/status") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real firewall route and authorization enabled={enabled}");
    Check(await GetAsync(client, SystemMonitorApiRoutes.PerformanceInfo) == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real performance route and authorization enabled={enabled}");
    Check(await PostAsync(client, $"{RelaxKonOSEndpoints.PerformanceHubPath}/negotiate?negotiateVersion=1") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real performance hub route and authorization enabled={enabled}");
    // Network addresses stay a Core route: the proxy application reads them without reading metrics.
    Check(await GetAsync(client, SystemMonitorApiRoutes.NetworkAddresses) == HttpStatusCode.Unauthorized,
        $"core network address route survives module state enabled={enabled}");
    Check(await GetAsync(client, $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/git/repositories") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real git repository route and authorization enabled={enabled}");
    Check(await PostAsync(client, $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/git/builds/resolve") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real git build route and authorization enabled={enabled}");
    Check(await GetAsync(client, $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/docker/status") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real docker manager route and authorization enabled={enabled}");
    Check(await GetAsync(client, $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/docker/proxy") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real docker proxy route and authorization enabled={enabled}");
    Check(await GetAsync(client, "/api/v1.0/certificates") == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound),
        $"real certificate route and authorization enabled={enabled}");
    await app.StopAsync();
}

Console.WriteLine("Server module checks passed.");

sealed class FakeModule(string id, IReadOnlyList<string> requires, IReadOnlyList<string> capabilities) : IServerModule
{
    public string Id => id;
    public IReadOnlyList<string> Capabilities => capabilities;
    public IReadOnlyList<string> Requires => requires;
    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context) { }
    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) { }
}

sealed class NoAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}

sealed class NoElevation : RelaxKonOS.Server.Privileged.IHostElevationSessionStore
{
    public bool IsGranted(System.Security.Claims.ClaimsPrincipal principal,
        RelaxKonOS.Protocol.Privileged.HostElevationCapability capability, string target) => false;

    public DateTimeOffset Grant(System.Security.Claims.ClaimsPrincipal principal,
        RelaxKonOS.Protocol.Privileged.HostElevationCapability capability, string target, bool includeDescendants,
        string authenticationMethod, string? correlationId = null) => DateTimeOffset.MinValue;

    public void Revoke(System.Security.Claims.ClaimsPrincipal principal) { }
}

sealed class NoOperationalEvents : RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher
{
    public Task PublishAsync(RelaxKonOS.Server.EventAlerts.OperationalEventSignal signal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Stands in for the Core-owned TLS store: the module checks never read real key material.</summary>
sealed class NoCertificateStore : RelaxKonOS.Server.Certificate.ICertificateStore
{
    private static NotSupportedException Missing() => new("The module checks never touch stored certificates.");

    public Task SaveAsync(RelaxKonOS.Server.Certificate.CertificateMaterial material, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Server.Certificate.StoredCertificate?> GetAsync(Guid certificateId, CancellationToken ct) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Server.Certificate.StoredCertificate>> ListAsync(CancellationToken ct) => throw Missing();
    public Task UpdateRenewalWindowAsync(Guid certificateId, RelaxKonOS.Server.Certificate.AcmeRenewalWindow? window, CancellationToken ct) => throw Missing();
    public Task UpdateRenewalOutcomeAsync(Guid certificateId, DateTimeOffset? renewedAt, string? problemCode, CancellationToken ct) => throw Missing();
    public Task UpdateStatusAsync(Guid certificateId, RelaxKonOS.Protocol.Certificates.CertificateStatus status, CancellationToken ct) => throw Missing();
    public Task<System.Security.Cryptography.X509Certificates.X509Certificate2?> LoadCurrentAsync(Guid certificateId, CancellationToken ct) => throw Missing();
    public Task<System.Security.Cryptography.X509Certificates.X509Certificate2?> LoadVersionAsync(Guid certificateId, string version, CancellationToken ct) => throw Missing();
    public Task<(string FullChainPath, string PrivateKeyPath)?> GetNginxPathsAsync(Guid certificateId, CancellationToken ct) => throw Missing();
    public Task DeleteAsync(Guid certificateId, CancellationToken ct) => throw Missing();
}

/// <summary>Stands in for the Core-owned web server manager so the module routes can be mapped.</summary>
sealed class NoWebServerManager : RelaxKonOS.Server.WebServer.IWebServerManager
{
    private static NotSupportedException Missing() => new("The module checks never reach a real web server.");

    public Task<IReadOnlyList<RelaxKonOS.Protocol.WebServers.WebServerDto>> DiscoverAsync(CancellationToken ct) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.WebServers.WebServerDto>> ListAsync(CancellationToken ct) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.WebServers.WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, string idempotencyKey,
        RelaxKonOS.Protocol.WebServers.IntegrateWebServerRequest request, string? actor, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerOperationDto?> ApplyLifecycleAsync(string instanceId,
        RelaxKonOS.Protocol.WebServers.WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken ct) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.WebServers.WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken ct) => throw Missing();
    public Task<RelaxKonOS.Protocol.WebServers.WebServerSiteDto?> UpsertSiteAsync(string instanceId,
        RelaxKonOS.Protocol.WebServers.UpsertWebServerSiteRequest request, CancellationToken ct) => throw Missing();
    public Task<bool?> DeleteSiteAsync(string instanceId, string siteId,
        RelaxKonOS.Protocol.WebServers.DeleteWebServerSiteRequest request, CancellationToken ct) => throw Missing();
}

/// <summary>Stands in for the Core-owned Docker transport so the manager routes can be mapped.</summary>
sealed class NoDockerEngine : RelaxKonOS.Server.Docker.IDockerEngineService
{
    private static NotSupportedException Missing() => new("The module checks never reach the Docker engine.");

    public Task<RelaxKonOS.Protocol.Docker.DockerStatusDto> GetStatusAsync(CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Server.Docker.DockerEngineProxyState?> GetProxyStateAsync(CancellationToken ct = default) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.Docker.DockerContainerDto>> ListContainersAsync(CancellationToken ct = default) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.Docker.DockerImageDto>> ListImagesAsync(CancellationToken ct = default) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.Docker.DockerNetworkDto>> ListNetworksAsync(CancellationToken ct = default) => throw Missing();
    public Task<IReadOnlyList<RelaxKonOS.Protocol.Docker.DockerVolumeDto>> ListVolumesAsync(CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> ApplyContainerActionAsync(string containerId, string action,
        RelaxKonOS.Protocol.Docker.DockerContainerActionRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> PullImageAsync(RelaxKonOS.Protocol.Docker.DockerImageOperationRequest request,
        string? resolvedImageReference = null, CancellationToken ct = default, Action<string>? onOutput = null) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> DeleteImageAsync(string imageId,
        RelaxKonOS.Protocol.Docker.DockerImageOperationRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> CreateContainerAsync(
        RelaxKonOS.Protocol.Docker.DockerContainerCreateRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> UpdateContainerAsync(string id,
        RelaxKonOS.Protocol.Docker.DockerContainerUpdateRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> CreateNetworkAsync(
        RelaxKonOS.Protocol.Docker.DockerNetworkCreateRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> CreateVolumeAsync(
        RelaxKonOS.Protocol.Docker.DockerVolumeCreateRequest request, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> BuildImageAsync(RelaxKonOS.Protocol.Docker.DockerBuildRequest request,
        bool includeBuildOutput = false, CancellationToken ct = default, Action<string>? onOutput = null) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerImageArchiveDto?> ExportImageAsync(string imageId, CancellationToken ct = default) => throw Missing();
    public Task<RelaxKonOS.Protocol.Docker.DockerOperationResult> ImportImageAsync(
        RelaxKonOS.Protocol.Docker.DockerImageArchiveDto archive, CancellationToken ct = default) => throw Missing();
}
