using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;
using System.Security.Claims;
using System.Text.Encodings.Web;

// Fixture writer for the process-level deactivation check, so the ledger under test is produced by the
if (args is ["--git-build-command-child", var childMarker])
{
    File.WriteAllText(childMarker, Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
// real store instead of hand-written JSON:
//   RelaxKonOS.ServerModules.Tests.exe --write-unresolved-stack <absolute directory>
// Then start the Server with DockerCompose__DataDirectory=<directory> and Modules__Docker__Enabled=false:
// it must refuse to start and name the operation.
if (args is ["--write-module-recovery-fixture", var recoveryDirectory])
{
    recoveryDirectory = Path.GetFullPath(recoveryDirectory);
    Directory.CreateDirectory(recoveryDirectory);
    File.WriteAllText(Path.Combine(recoveryDirectory, "appsettings.host.json"), "{\"ModuleManagement\":{\"Authority\":\"managed\"}}");
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ModuleManagement:Authority"] = "managed" }).Build();
    using var store = new ModuleManagementStore(configuration, recoveryDirectory);
    var modules = ServerModules.Create(configuration);
    var facts = modules.EnabledIds.Select(id => new ServerModuleDto(id, true, [], [], [], [])).ToArray();
    var plan = store.Preview("fixture", new(store.ServiceId, ServerModuleAction.Disable, [ServerModules.GitId],
        modules.EnabledIds.Where(id => id != ServerModules.GitId).ToArray(), store.Revision, "fixture-disable"), facts);
    store.Apply("fixture", plan.PlanId, facts);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ModuleManagementStore.ReadRecoveryReport(recoveryDirectory), RelaxKonOSJsonOptions.Default));
    return;
}
if (args is ["--write-unresolved-stack", var fixtureDirectory])
{
    var fixtureBuilder = WebApplication.CreateBuilder();
    var fixtureOptions = Options.Create(
        new RelaxKonOS.Server.Docker.DockerComposeOptions { DataDirectory = fixtureDirectory });
    var fixtureStore = new RelaxKonOS.Server.Docker.DockerStackOperationStore(fixtureBuilder.Environment, fixtureOptions);
    var fixtureEntry = fixtureStore.Create("gate", RelaxKonOS.Protocol.Docker.DockerStackOperationKind.Deploy,
        "fixture", "gate-" + Guid.NewGuid().ToString("N"),
        RelaxKonOS.Server.Docker.DockerStackValidation.Reference("deploy|gate|v1"), out _);
    fixtureStore.Update(fixtureEntry.Operation.OperationId, current => current with
    {
        State = RelaxKonOS.Protocol.Docker.DockerStackOperationState.Running,
        Stage = RelaxKonOS.Protocol.Docker.DockerStackOperationStage.Applying,
        StartedAt = DateTimeOffset.UtcNow,
    }, "started");
    Console.WriteLine(fixtureEntry.Operation.OperationId);
    return;
}

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

/// <summary>Runs an async check and returns the diagnostic it produced, or null when it passed.</summary>
static async Task<string?> FailureAsync(Func<Task> action)
{
    try { await action(); return null; }
    catch (InvalidOperationException exception) { return exception.Message; }
}

static IConfiguration Config(params (string Key, string? Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => item.Value)).Build();

var empty = new ConfigurationBuilder().Build();
var defaults = ServerModules.Create(empty);
ModuleManagementChecks.Run(Check);
ModulePolicyManagementChecks.Run(Check);
await ModuleRecoveryChecks.RunAsync(Check);
await ModuleRecoveryHostChecks.RunAsync(Check);
await FirewallResidencyChecks.RunAsync(Check);
await ModuleDependencyResidencyChecks.RunAsync(Check);
await ModuleStreamingChecks.RunAsync(Check);
await ModuleBackgroundWorkChecks.RunAsync(Check);
await GitDeactivationChecks.RunAsync(Check);
await ModulePackageChecks.RunAsync(Check);
await ModuleManagementHttpChecks.RunAsync(Check);
await ModuleRuntimeManagementChecks.RunAsync(Check);

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
    Check(modules.EnabledIds.Count == defaults.EnabledIds.Count - 1, $"other modules stay enabled when {disabled} is disabled");
    Check(ServerModules.Create(Config(($"Modules:{name}:Enabled", "true"))).IsEnabled(disabled),
        $"explicit true keeps {disabled}");
}

// The one real hard dependency in the catalog: website publication binds a site to a certificate.
Reject(() => ServerModules.Create(Config(("Modules:Certificates:Enabled", "false"))),
    "disabling a module that another enabled module requires stops startup");
Check(ServerModules.Create(Config(("Modules:Certificates:Enabled", "false"), ("Modules:WebServer:Enabled", "false")))
        .EnabledIds.Count == defaults.EnabledIds.Count - 2,
    "the dependent module can be disabled together with its dependency");
Check(ServerModules.Create(Config(("Modules:WebServer:Enabled", "false"))).IsEnabled(ServerModules.CertificatesId),
    "disabling the dependent module leaves the certificate module enabled");

Check(ServerModules.Create(Config(("Modules:Firewall:Enabled", "false")))
        .IsEnabled(ServerModules.SystemPerformanceId),
    "disabling one module never cascades into an unrelated module");

// Capability sets follow the snapshot: a disabled module contributes nothing.
Check(defaults.Capabilities.SetEquals([ServerCapabilities.Terminal, ServerCapabilities.Firewall,
        ServerCapabilities.Metrics, ServerCapabilities.Processes, ServerCapabilities.Git, ServerCapabilities.Docker,
        ServerCapabilities.Certificates, ServerCapabilities.WebServer, ServerCapabilities.Tailscale,
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

// Inventory: every official module, plus the two reasons a disable request would be refused.
{
    var hostBuilder = WebApplication.CreateBuilder();
    var context = ServerModuleContext.Create(hostBuilder.Configuration, hostBuilder.Environment, "sqlite");
    var root = Path.Combine(Path.GetTempPath(), "rk-inventory-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var collection = new ServiceCollection();
        collection.AddDataProtection();
        collection.AddSingleton<RelaxKonOS.Server.Proxy.IProxyPlatformPaths>(new GateProxyPaths(root));
        collection.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore>();
        collection.AddSingleton(new RelaxKonOS.Server.Docker.DockerStackOperationStore(hostBuilder.Environment,
            Microsoft.Extensions.Options.Options.Create(new RelaxKonOS.Server.Docker.DockerComposeOptions { DataDirectory = root })));
        using var services = collection.BuildServiceProvider();

        var inventory = await ServerModules.Create(new ConfigurationBuilder().Build())
            .DescribeAsync(services, context, CancellationToken.None);
        Check(inventory.Count == defaults.EnabledIds.Count && inventory.All(item => item.EffectiveEnabled && item.OrphanedWork.Count == 0),
            "every official module is listed as enabled with nothing outstanding");
        Check(inventory.Single(item => item.Id == ServerModules.CertificatesId).RequiredBy.SequenceEqual([ServerModules.WebServerId]),
            "an enabled dependent is reported as a reason its dependency cannot be disabled");

        var withoutWebServer = await ServerModules.Create(Config(("Modules:WebServer:Enabled", "false")))
            .DescribeAsync(services, context, CancellationToken.None);
        var webServer = withoutWebServer.Single(item => item.Id == ServerModules.WebServerId);
        Check(!webServer.EffectiveEnabled && webServer.OrphanedWork.Count == 0 && webServer.Capabilities.Count == 1,
            "a disabled module is reported as disabled, keeps its declared capability, and is not inspected");
        Check(withoutWebServer.Single(item => item.Id == ServerModules.CertificatesId).RequiredBy.Count == 0,
            "a disabled dependent stops being a reason to keep its dependency enabled");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

// The deactivation gate: disabling a module that owns durable work must stop startup while that work
// is unresolved, because nothing in the process can reconcile it afterwards. The gate is deliberately
// override-free: finishing the work is the only way past it.
{
    var hostBuilder = WebApplication.CreateBuilder();
    var context = ServerModuleContext.Create(hostBuilder.Configuration, hostBuilder.Environment, "sqlite");
    using var services = new ServiceCollection().BuildServiceProvider();

    var blocked = new FakeDurableModule("relaxkonos.blocked",
        new ServerModuleOrphanedWork("relaxkonos.blocked", "compose stack operation 6f4c is still Running."));
    var clean = new FakeDurableModule("relaxkonos.clean");
    var enabledDurable = new FakeDurableModule("relaxkonos.enabled",
        new ServerModuleOrphanedWork("relaxkonos.enabled", "must never be inspected"));

    var gated = ServerModules.Resolve([blocked, clean, enabledDurable],
        new HashSet<string> { "relaxkonos.enabled" });
    var message = await FailureAsync(() => gated.ValidateDeactivationAsync(services, context, CancellationToken.None));
    Check(message is not null && message.Contains("relaxkonos.blocked") && message.Contains("6f4c"),
        "a disabled module with unresolved durable work stops startup and names the operation");
    Check(clean.Inspections == 1 && enabledDurable.Inspections == 0,
        "the gate asks every disabled guard and never an enabled module");

    var allEnabled = ServerModules.Resolve([blocked, clean, enabledDurable],
        new HashSet<string> { "relaxkonos.blocked", "relaxkonos.clean", "relaxkonos.enabled" });
    var inspectedWhileGated = blocked.Inspections;
    await allEnabled.ValidateDeactivationAsync(services, context, CancellationToken.None);
    Check(blocked.Inspections == inspectedWhileGated, "an enabled durable module is not inspected for orphaned work");
}

// The Docker guard reads the real ledger instead of trusting a flag.
{
    var hostBuilder = WebApplication.CreateBuilder();
    var context = ServerModuleContext.Create(hostBuilder.Configuration, hostBuilder.Environment, "sqlite");
    var root = Path.Combine(Path.GetTempPath(), "rk-gate-docker-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new RelaxKonOS.Server.Docker.DockerComposeOptions { DataDirectory = root });
        var store = new RelaxKonOS.Server.Docker.DockerStackOperationStore(hostBuilder.Environment, options);
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var guard = new RelaxKonOS.Server.Startup.Modules.DockerModule();

        var nothingOrphaned = await guard.InspectOrphanedWorkAsync(services, context, CancellationToken.None);
        Check(nothingOrphaned.Count == 0, "an empty compose stack ledger leaves nothing orphaned");

        var operation = store.Create("web", RelaxKonOS.Protocol.Docker.DockerStackOperationKind.Deploy, "actor",
            "gate-key", RelaxKonOS.Server.Docker.DockerStackValidation.Reference("deploy|web|v1"), out _).Operation;
        store.Update(operation.OperationId, current => current with
        {
            State = RelaxKonOS.Protocol.Docker.DockerStackOperationState.Running,
            Stage = RelaxKonOS.Protocol.Docker.DockerStackOperationStage.Applying,
            StartedAt = DateTimeOffset.UtcNow,
        }, "started");

        var orphaned = await guard.InspectOrphanedWorkAsync(services, context, CancellationToken.None);
        Check(orphaned.Count == 1 && orphaned[0].ModuleId == ServerModules.DockerId
                && orphaned[0].Description.Contains(operation.OperationId.ToString()),
            "the docker guard reports the queued or running compose stack operation");

        var inventory = await ServerModules.Resolve(new IServerModule[] { guard },
                new HashSet<string> { ServerModules.DockerId })
            .DescribeAsync(services, context, CancellationToken.None);
        Check(inventory.Count == 1 && inventory[0].OrphanedWork.Count == 1,
            "the module inventory publishes the same unresolved work the gate would refuse");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

// The web server guard reads its own ledger, and asking must not reconcile anything by itself.
{
    var root = Path.Combine(Path.GetTempPath(), "rk-gate-webserver-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "data"));
    try
    {
        var hostBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        var context = ServerModuleContext.Create(hostBuilder.Configuration, hostBuilder.Environment, "sqlite");
        var store = new RelaxKonOS.Server.WebsitePublishing.WebsitePublicationStore(hostBuilder.Environment);
        store.Start(new RelaxKonOS.Protocol.WebsitePublishing.PublishWebsiteRequest(Guid.NewGuid(),
                "nginx-1", "example.test", null, null, true, true, true),
            "actor", "gate-key", out _);

        var guard = new RelaxKonOS.Server.Startup.Modules.WebServerModule();
        using var services = new ServiceCollection().BuildServiceProvider();
        var orphaned = await guard.InspectOrphanedWorkAsync(services, context, CancellationToken.None);
        Check(orphaned.Count == 1 && orphaned[0].ModuleId == ServerModules.WebServerId
                && orphaned[0].Description.Contains("example.test"),
            "the web server guard reports the queued site publication");
        Check(new RelaxKonOS.Server.WebsitePublishing.WebsitePublicationStore(hostBuilder.Environment).Pending().Count == 1,
            "inspecting the publication ledger never interrupts the operation");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

// The proxy guard treats an unreadable publication authority as unknown, never as "nothing to do".
{
    var hostBuilder = WebApplication.CreateBuilder();
    var context = ServerModuleContext.Create(hostBuilder.Configuration, hostBuilder.Environment, "sqlite");
    var root = Path.Combine(Path.GetTempPath(), "rk-gate-proxy-" + Guid.NewGuid().ToString("N"));
    var state = Path.Combine(root, "state");
    Directory.CreateDirectory(state);
    try
    {
        var collection = new ServiceCollection();
        collection.AddDataProtection();
        collection.AddSingleton<RelaxKonOS.Server.Proxy.IProxyPlatformPaths>(new GateProxyPaths(root));
        collection.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore>();
        using var services = collection.BuildServiceProvider();
        var guard = new RelaxKonOS.Server.Startup.Modules.ProxyModule();

        var clean = await guard.InspectOrphanedWorkAsync(services, context, CancellationToken.None);
        Check(clean.Count == 0, "no published runtime leaves nothing orphaned in the proxy module");

        await File.WriteAllBytesAsync(Path.Combine(state, "mihomo-publication.protected"), [1, 2, 3, 4]);
        var unknown = await guard.InspectOrphanedWorkAsync(services, context, CancellationToken.None);
        Check(unknown.Count == 1 && unknown[0].Description.Contains("cannot be read"),
            "an unreadable publication authority blocks deactivation instead of reporting no work");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
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
    // Core-owned Compose ledger: the deactivation gate reads it while the Docker manager is disabled,
    // and the module's coordinator is its only writer.
    builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(builder.Configuration.GetSection("DockerCompose"));
    builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
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
    Check(builder.Services.Any(service => service.ServiceType == typeof(RelaxKonOS.Server.Git.IGitBuildService)) == enabled,
        $"git build ledger gate enabled={enabled}");
    // Residency boundary: the Host holds lease-taking gates, never the releasable implementations, so
    // one process can drop the module's object graph without rebuilding the root container.
    Check(builder.Services.All(service => service.ServiceType != typeof(RelaxKonOS.Server.Git.GitBuildService)
            && service.ServiceType != typeof(RelaxKonOS.Server.Git.LocalGitRepositoryService)
            && service.ServiceType != typeof(RelaxKonOS.Server.Git.HostGitCli)),
        $"releasable git implementations are not root singletons enabled={enabled}");
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
            ? typeof(RelaxKonOS.Server.Git.ResidentDeploymentImageBuildSource)
            : typeof(RelaxKonOS.Server.ApplicationDeployments.UnavailableDeploymentImageBuildSource)),
        $"deployment git build port uses the module gate only when enabled={enabled}");
    var unresolved = imageBuilds.Resolve(Guid.NewGuid(), Guid.NewGuid());
    Check(unresolved.Status == (enabled
            ? RelaxKonOS.Server.ApplicationDeployments.DeploymentImageBuildStatus.NotFound
            : RelaxKonOS.Server.ApplicationDeployments.DeploymentImageBuildStatus.Unavailable),
        $"deployment git build port distinguishes a missing build from a missing module enabled={enabled}");
    Check(unresolved.Status != RelaxKonOS.Server.ApplicationDeployments.DeploymentImageBuildStatus.Available,
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

// Residency: the default rule, the state machine and the Git pilot lease gates
{
    var residencyIds = new HashSet<string>(StringComparer.Ordinal) { ServerModules.GitId };
    var residencyContextBuilder = WebApplication.CreateBuilder();
    var residencyContext = ServerModuleContext.Create(residencyContextBuilder.Configuration,
        residencyContextBuilder.Environment, "sqlite");

    var residencyDefaults = ModuleResidencyOptions.Parse(empty, residencyIds, residencyIds);
    Check(residencyDefaults.Default.Strategy == ModuleResidencyStrategy.IdleDelayed
          && residencyDefaults.Default.IdleGracePeriod == TimeSpan.FromMinutes(5),
        "residency defaults to idle delayed release with a provisional five minute grace period");
    Check(residencyDefaults.For(ServerModules.GitId).Strategy == ModuleResidencyStrategy.IdleDelayed,
        "a module without an override inherits the server-level rule");

    var overrideOptions = ModuleResidencyOptions.Parse(Config(
        ("ModuleResidency:Strategy", "ReleaseOnLogout"),
        ("ModuleResidency:IdleGraceSeconds", "30"),
        ("ModuleResidency:Modules:relaxkonos.git:IdleGraceSeconds", "5")), residencyIds, residencyIds);
    Check(overrideOptions.For(ServerModules.GitId) is { Strategy: ModuleResidencyStrategy.ReleaseOnLogout, IdleGracePeriod.TotalSeconds: 5 }
          && overrideOptions.Default.IdleGracePeriod.TotalSeconds == 30,
        "a per-module override inherits the strategy and changes only its own grace period");

    Reject(() => ModuleResidencyOptions.Parse(Config(("ModuleResidency:Strategy", "Sometimes")), residencyIds, residencyIds),
        "an unknown residency strategy is rejected");
    Reject(() => ModuleResidencyOptions.Parse(Config(("ModuleResidency:IdleGraceSeconds", "-1")), residencyIds, residencyIds),
        "a negative grace period is rejected");
    Reject(() => ModuleResidencyOptions.Parse(Config(("ModuleResidency:IdleGrace", "1")), residencyIds, residencyIds),
        "an unknown residency setting is rejected");
    Reject(() => ModuleResidencyOptions.Parse(Config(
            ("ModuleResidency:Modules:relaxkonos.unknown:IdleGraceSeconds", "1")), residencyIds, residencyIds),
        "residency for an unknown module is rejected");
    Reject(() => ModuleResidencyOptions.Parse(Config(
            ("ModuleResidency:Modules:relaxkonos.terminal:IdleGraceSeconds", "1")),
            new HashSet<string>(StringComparer.Ordinal) { ServerModules.GitId, ServerModules.TerminalId }, residencyIds),
        "residency for a module without a release boundary is rejected");

    // A coordinator that can merge concurrent first requests: the fake factory is slow on purpose.
    var slowFactory = new FakeRuntimeFactory(ServerModules.GitId);
    var residencyServices = new ServiceCollection().AddLogging().AddHttpContextAccessor().BuildServiceProvider();
    var residencyLogger = residencyServices.GetRequiredService<ILogger<ModuleResidencyCoordinator>>();
    var zeroGrace = ModuleResidencyOptions.Parse(Config(("ModuleResidency:IdleGraceSeconds", "0")), residencyIds, residencyIds);
    var limited = ModuleResidencyOptions.Parse(Config(("ModuleResidency:MaxWaitingPerModule", "1")), residencyIds, residencyIds);
    var ready = new TaskCompletionSource<IServerModuleRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
    var queuedFactory = new FakeRuntimeFactory(ServerModules.GitId) { Pending = ready.Task };
    await using (var bounded = new ModuleResidencyCoordinator([queuedFactory], limited, residencyServices, residencyContext, residencyLogger))
    {
        using var cancelled = new CancellationTokenSource();
        var waiting = bounded.AcquireAsync(ServerModules.GitId, cancelled.Token);
        var overload = await UnavailableAsync(() => bounded.AcquireAsync(ServerModules.GitId));
        Check(overload?.Code == "module-wake-overloaded" && queuedFactory.Created == 1, "bounded wake queue rejects overload without a second load");
        cancelled.Cancel();
        try { await waiting; throw new Exception("cancelled waiter acquired a lease"); }
        catch (OperationCanceledException) { }
        var second = bounded.AcquireAsync(ServerModules.GitId);
        ready.SetResult(new FakeRuntime(ServerModules.GitId));
        using var lease = await second;
        Check(queuedFactory.Created == 1, "cancelled wake waiter frees admission without cancelling shared initialization");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        try { using var _ = await bounded.AcquireAsync(ServerModules.GitId, alreadyCancelled.Token); throw new Exception("cancelled request admitted"); }
        catch (OperationCanceledException) { Check(bounded.Describe(ServerModules.GitId)!.ActiveLeases == 1, "cancelled request cannot acquire an active runtime lease"); }
    }
    await using (var coordinator = new ModuleResidencyCoordinator([slowFactory], zeroGrace, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant,
            "a releasable module starts dormant");
        Check(slowFactory.Created == 0, "reading residency state never loads the module");

        var first = await Task.WhenAll(coordinator.AcquireAsync(ServerModules.GitId),
            coordinator.AcquireAsync(ServerModules.GitId));
        Check(slowFactory.Created == 1 && coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Active,
            "concurrent first leases are merged into one load");
        Check(coordinator.Describe(ServerModules.GitId)!.ActiveLeases == 2,
            "the residency snapshot reports the active lease count");

        await coordinator.SweepAsync();
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Active,
            "a module with an active lease is never released");

        first[0].Dispose();
        first[1].Dispose();
        await coordinator.SweepAsync();
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant
              && slowFactory.Last!.Disposed == 1,
            "the last lease plus the grace period releases the module runtime");

        var second = await coordinator.AcquireAsync(ServerModules.GitId);
        Check(slowFactory.Created == 2 && coordinator.Describe(ServerModules.GitId)!.LastWakeMilliseconds is >= 0,
            "a dormant module wakes on the next request with a new instance and a recorded wake time");
        second.Dispose();
        await coordinator.SweepAsync();
    }

    // A blocked release keeps the module and reports why; a failing load fails closed with a retry hint.
    var blockedFactory = new FakeRuntimeFactory(ServerModules.GitId)
        { Blocker = new ModuleUnloadBlocker("one git build is still running") };
    await using (var coordinator = new ModuleResidencyCoordinator([blockedFactory], zeroGrace, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        var lease = await coordinator.AcquireAsync(ServerModules.GitId);
        lease.Dispose();
        await coordinator.SweepAsync();
        var snapshot = coordinator.Describe(ServerModules.GitId)!;
        Check(snapshot.State == ModuleResidencyState.Active
              && snapshot.BlockedBy == "one git build is still running"
              && blockedFactory.Last!.Disposed == 0,
            "a runtime that reports unfinished work is kept alive with the reason");
    }

    var failingFactory = new FakeRuntimeFactory(ServerModules.GitId) { Failure = new InvalidOperationException("boom") };
    await using (var coordinator = new ModuleResidencyCoordinator([failingFactory], zeroGrace, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        var failure = await UnavailableAsync(() => coordinator.AcquireAsync(ServerModules.GitId));
        Check(failure is { Code: "module-load-failed" } && failingFactory.Created == 1,
            "a failed load surfaces exactly one attempt to the waiting requests");
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Faulted
              && coordinator.Describe(ServerModules.GitId)!.Failure!.Contains("boom", StringComparison.Ordinal),
            "a failed load is reported as faulted with its cause");
        Check(coordinator.TryAcquire(ServerModules.GitId, TimeSpan.FromMilliseconds(50)) is null,
            "a synchronous cross-domain call fails closed instead of blocking on a broken module");
    }

    var hangSource = new TaskCompletionSource<IServerModuleRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
    var hangingFactory = new FakeRuntimeFactory(ServerModules.GitId) { Pending = hangSource.Task };
    var shortWake = ModuleResidencyOptions.Parse(Config(("ModuleResidency:WakeTimeoutSeconds", "0"),
        ("ModuleResidency:IdleGraceSeconds", "0")), residencyIds, residencyIds);
    await using (var coordinator = new ModuleResidencyCoordinator([hangingFactory], shortWake, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        var failure = await UnavailableAsync(() => coordinator.AcquireAsync(ServerModules.GitId));
        Check(failure is { Code: "module-wake-timeout" },
            "a wake that exceeds the timeout returns a stable unavailable result");
        Check(hangingFactory.Created == 1, "a timed out wait does not cancel the shared load for the next caller");
    }

    var keepFactory = new FakeRuntimeFactory(ServerModules.GitId);
    var keepOptions = ModuleResidencyOptions.Parse(Config(("ModuleResidency:Strategy", "KeepLoaded"),
        ("ModuleResidency:IdleGraceSeconds", "0")), residencyIds, residencyIds);
    await using (var coordinator = new ModuleResidencyCoordinator([keepFactory], keepOptions, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        var lease = await coordinator.AcquireAsync(ServerModules.GitId);
        lease.Dispose();
        await coordinator.SweepAsync();
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Active
              && keepFactory.Last!.Disposed == 0,
            "keep-loaded keeps the object graph after the last lease");
    }

    // Logout releases only modules that session used, and only after the last lease ends.
    var http = residencyServices.GetRequiredService<IHttpContextAccessor>();
    var sessionId = Guid.NewGuid();
    http.HttpContext = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sid", sessionId.ToString("D"))], "test")),
    };
    var logoutFactory = new FakeRuntimeFactory(ServerModules.GitId);
    var logoutOptions = ModuleResidencyOptions.Parse(Config(("ModuleResidency:Strategy", "ReleaseOnLogout"),
        ("ModuleResidency:IdleGraceSeconds", "3600")), residencyIds, residencyIds);
    await using (var coordinator = new ModuleResidencyCoordinator([logoutFactory], logoutOptions, residencyServices,
                     residencyContext, residencyLogger, sweepInterval: TimeSpan.FromMilliseconds(20)))
    {
        var lease = await coordinator.AcquireAsync(ServerModules.GitId);
        lease.Dispose();
        await coordinator.SweepAsync();
        Check(coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Active,
            "a release-on-logout module waits out its grace period while the session is open");
        coordinator.OnSessionEnded(sessionId);
        Check(await WaitForAsync(() => coordinator.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant),
            "logout releases the modules that session used without waiting for the grace period");
        Check(logoutFactory.Last!.Disposed == 1, "the released runtime is disposed exactly once");
    }

    // The inventory reports residency without waking anything.
    var inventoryFactory = new FakeRuntimeFactory(ServerModules.GitId);
    var inventoryServices = new ServiceCollection().AddSingleton(new ModuleResidencyCoordinator(
        [inventoryFactory], zeroGrace, residencyServices, residencyContext, residencyLogger,
        sweepInterval: TimeSpan.FromMilliseconds(20))).BuildServiceProvider();
    var inventory = await ServerModules.Create(empty).DescribeAsync(inventoryServices, residencyContext, CancellationToken.None);
    Check(inventory.Single(item => item.Id == ServerModules.GitId).Residency is { Releasable: true, State: "dormant" }
          && inventoryFactory.Created == 0,
        "the module inventory reports residency without loading the module");
    Check(inventory.Where(item => item.EffectiveEnabled && !defaults.KnownReleasableModuleIds.Contains(item.Id))
            .All(item => item.Residency is { Releasable: false, State: "resident" }),
        "a module without a release boundary is reported as resident, not as dormant");
}

// Git read-only pilot over real HTTP: load on a business request, release when idle, wake again
{
    var pilotRoot = Path.Combine(Path.GetTempPath(), "relaxkonos-residency-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(pilotRoot);
    var pilotBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = pilotRoot });
    pilotBuilder.Logging.ClearProviders();
    pilotBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    pilotBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ModuleResidency:IdleGraceSeconds"] = "0",
        ["GitBuilds:RootDirectory"] = "builds",
        // Only the pilot is part of this host: other modules bring their own recovery workers and
        // Core dependencies that would test them, not residency.
        ["Modules:Terminal:Enabled"] = "false",
        ["Modules:Firewall:Enabled"] = "false",
        ["Modules:SystemPerformance:Enabled"] = "false",
        ["Modules:Docker:Enabled"] = "false",
        ["Modules:Certificates:Enabled"] = "false",
        ["Modules:WebServer:Enabled"] = "false",
        ["Modules:Proxy:Enabled"] = "false",
        ["Modules:Tailscale:Enabled"] = "false",
    });
    pilotBuilder.Services.AddDbContextFactory<RelaxKonOS.Server.Storage.Sqlite.RelaxKonOSDbContext>(options =>
        options.UseSqlite("Data Source=" + Path.Combine(pilotRoot, "pilot.db")));
    pilotBuilder.Services.AddDataProtection();
    pilotBuilder.Services.AddHttpContextAccessor();
    pilotBuilder.Services.AddSignalR();
    pilotBuilder.Services.AddSingleton<RelaxKonOS.Server.UserExecution.IUserExecutionTransport, NoUserExecutionTransport>();
    pilotBuilder.Services.AddSingleton(new RelaxKonOS.Server.UserExecution.UserExecutionBackendSelection(
        RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper));
    pilotBuilder.Services.AddSingleton<IServerModeResolver>(new ServerModeResolver(pilotBuilder.Configuration,
        RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper));
    pilotBuilder.Services.AddAuthentication("test")
        .AddScheme<AuthenticationSchemeOptions, TestUserAuthentication>("test", _ => { });
    pilotBuilder.Services.AddAuthorization();
    var pilotContext = ServerModuleContext.Create(pilotBuilder.Configuration, pilotBuilder.Environment, "sqlite");
    var pilotModules = ServerModules.Create(pilotBuilder.Configuration);
    pilotModules.ConfigureServices(pilotBuilder, pilotContext);
    await using var pilotApp = pilotBuilder.Build();
    await using (var scope = pilotApp.Services.CreateAsyncScope())
    {
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<RelaxKonOS.Server.Storage.Sqlite.RelaxKonOSDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }
    var pilotResidency = pilotApp.Services.GetRequiredService<ModuleResidencyCoordinator>();
    pilotResidency.Start(pilotApp.Lifetime.ApplicationStopping);
    pilotApp.UseAuthentication();
    pilotApp.UseAuthorization();
    pilotModules.MapEndpoints(pilotApp, pilotContext);
    await pilotApp.StartAsync();
    var pilotAddress = pilotApp.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single();
    using var pilotClient = new HttpClient { BaseAddress = new Uri(pilotAddress) };

    Check(pilotResidency.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant,
        "the git pilot is dormant after startup");
    var repositoriesRoute = $"/{RelaxKonOSEndpoints.ApiVersionPrefix}/git/repositories";
    Check((await pilotClient.GetAsync(repositoriesRoute)).StatusCode == HttpStatusCode.OK,
        "a git read-only request succeeds and loads the pilot");
    Check(pilotResidency.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Active,
        "the pilot is active while it serves the request");
    Check(await WaitForAsync(() => pilotResidency.Describe(ServerModules.GitId)!.State == ModuleResidencyState.Dormant),
        "the idle sweep releases the pilot after the grace period");
    Check((await pilotClient.GetAsync(repositoriesRoute)).StatusCode == HttpStatusCode.OK,
        "the next git request after a release succeeds again");
    Check(pilotResidency.Describe(ServerModules.GitId)!.Generation == 2,
        "the wake after a release is a new runtime generation");
    await pilotApp.StopAsync();
}

static async Task<bool> WaitForAsync(Func<bool> predicate)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (predicate()) return true;
        await Task.Delay(10);
    }
    return predicate();
}

/// <summary>Runs a residency call and returns the typed unavailable diagnostic, or null when it passed.</summary>
static async Task<ModuleUnavailableException?> UnavailableAsync(Func<Task> action)
{
    try { await action(); return null; }
    catch (ModuleUnavailableException exception) { return exception; }
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

/// <summary>A module that claims durable work, so the gate can be exercised without real ledgers.</summary>
sealed class FakeDurableModule(string id, params ServerModuleOrphanedWork[] orphaned)
    : IServerModule, IServerModuleDeactivationGuard
{
    public int Inspections { get; private set; }
    public string Id => id;
    public IReadOnlyList<string> Capabilities => [];
    public IReadOnlyList<string> Requires => [];
    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context) { }
    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) { }

    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        Inspections++;
        return ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>(orphaned);
    }
}

/// <summary>Scratch directories so the proxy publication guard reads a file the test controls.</summary>
sealed class GateProxyPaths(string root) : RelaxKonOS.Server.Proxy.IProxyPlatformPaths
{
    public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "engines", engineId, "versions");
    public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "engines", engineId, "data");
    public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
    public string GetStateDirectory() => Path.Combine(root, "state");
    public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
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

sealed class FakeRuntimeFactory(string moduleId) : IServerModuleRuntimeFactory
{
    public string ModuleId { get; } = moduleId;
    public int Created;
    public FakeRuntime? Last;
    public Exception? Failure;
    public ModuleUnloadBlocker? Blocker;
    public Task<IServerModuleRuntime>? Pending;
    public TimeSpan CreateDelay = TimeSpan.FromMilliseconds(25);

    public async ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Created);
        if (Pending is not null) return await Pending.WaitAsync(cancellationToken);
        if (CreateDelay > TimeSpan.Zero) await Task.Delay(CreateDelay, cancellationToken);
        if (Failure is not null) throw Failure;
        Last = new FakeRuntime(ModuleId) { Blocker = Blocker };
        return Last;
    }
}

sealed class FakeRuntime(string moduleId) : IServerModuleRuntime
{
    public string ModuleId { get; } = moduleId;
    public int Disposed;
    public ModuleUnloadBlocker? Blocker { get; init; }

    public object Resolve(Type serviceType) => this;

    public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Blocker);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Disposed);
        return ValueTask.CompletedTask;
    }
}

sealed class TestUserAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim("sid", Guid.NewGuid().ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

sealed class NoUserExecutionTransport : RelaxKonOS.Server.UserExecution.IUserExecutionTransport
{
    public Task<RelaxKonOS.Protocol.UserExecution.UserExecutionResult> ExecuteAsync(
        RelaxKonOS.Protocol.UserExecution.UserExecutionRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("user execution is not available in this test host");
}
