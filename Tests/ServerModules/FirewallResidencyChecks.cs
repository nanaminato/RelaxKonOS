using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class FirewallResidencyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "firewall-residency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Logging.ClearProviders();
            var catalog = ServerModules.Create(new ConfigurationBuilder().Build());
            builder.Configuration.AddInMemoryCollection(catalog.KnownModuleIds.ToDictionary(
                id => "Modules:" + ServerModules.ConfigurationName(id) + ":Enabled", id => (string?)(id == ServerModules.FirewallId).ToString()));
            var modules = ServerModules.Create(builder.Configuration);
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var transport = new FirewallTransport();
            var mode = new FirewallMode(builder.Configuration);
            var elevations = new FirewallElevation();
            builder.Services.AddSingleton<IPrivilegedOperationTransport>(transport);
            builder.Services.AddSingleton<IServerModeResolver>(mode);
            builder.Services.AddSingleton<IHostElevationSessionStore>(elevations);
            builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
            builder.Services.AddAuthorization();
            modules.ConfigureServices(builder, context);
            await using var app = builder.Build();
            app.UseServerProblemMapping(); app.UseAuthentication(); app.UseAuthorization();
            modules.MapEndpoints(app, context);
            app.MapGet("/inventory", () => modules.DescribeAsync(app.Services, context, CancellationToken.None)).RequireAuthorization();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            const string statusRoute = "/api/v1.0/firewall/status";
            check((await client.GetAsync(statusRoute)).StatusCode == HttpStatusCode.Unauthorized && transport.Calls == 0
                && coordinator.Describe(ServerModules.FirewallId)!.Generation == 0,
                "unauthenticated firewall request does not load its facade or call the Helper");
            client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
            var facts = (await client.GetFromJsonAsync<ServerModuleDto[]>("/inventory", RelaxKonOSJsonOptions.Default))!;
            check(facts.Single(module => module.Id == ServerModules.FirewallId).Residency is { State: "dormant", Releasable: true }
                && transport.Calls == 0, "firewall inventory reports the dormant object boundary without waking it");
            mode.Allowed = false;
            check((await client.GetAsync(statusRoute)).StatusCode == HttpStatusCode.Forbidden && transport.Calls == 0
                && coordinator.Describe(ServerModules.FirewallId)!.Generation == 0,
                "deployment feature denial precedes firewall runtime loading");
            mode.Allowed = true;
            var denied = await client.PutAsJsonAsync("/api/v1.0/firewall/enabled", new { enabled = true });
            var denial = (await denied.Content.ReadFromJsonAsync<FirewallOperationResult>(RelaxKonOSJsonOptions.Default))!;
            check(!denial.Success && transport.Calls == 0 && coordinator.Describe(ServerModules.FirewallId)!.Generation == 0,
                "firewall mutation without elevation never wakes a module or calls the Helper");
            var status = await client.GetFromJsonAsync<FirewallStatusDto>(statusRoute, RelaxKonOSJsonOptions.Default);
            check(status is { IsAvailable: true, IsEnabled: true } && transport.Calls == 1
                && coordinator.Describe(ServerModules.FirewallId) is { State: ModuleResidencyState.Active, ActiveLeases: 0 },
                "authorized firewall read wakes the real platform facade and returns its fully materialized status");
            var generation = coordinator.Describe(ServerModules.FirewallId)!.Generation;
            check(await coordinator.TrySleepAsync(ServerModules.FirewallId, CancellationToken.None) is null
                && !transport.Disposed && coordinator.Describe(ServerModules.FirewallId)!.State == ModuleResidencyState.Dormant,
                "firewall object release leaves the Core Helper transport and external firewall untouched");
            await client.GetAsync(statusRoute);
            check(coordinator.Describe(ServerModules.FirewallId)!.Generation > generation && transport.Calls == 2,
                "the next firewall request creates a new facade generation and executes the read once");
            elevations.Allowed = true;
            await client.PutAsJsonAsync("/api/v1.0/firewall/enabled", new { enabled = true });
            check(transport.Calls == 3 && transport.Last?.Operation is PrivilegedOperationKind.FirewallUfwSetEnabled or PrivilegedOperationKind.FirewallWindowsSetEnabled,
                "elevated firewall mutation crosses the existing structured Helper boundary exactly once");
            transport.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = new CancellationTokenSource();
            var held = app.Services.GetRequiredService<IHostFirewallService>().GetStatusAsync(cancelled.Token);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(await coordinator.TrySleepAsync(ServerModules.FirewallId, CancellationToken.None) == "module-active-work",
                "a firewall request in the Helper holds a lease and blocks memory release");
            cancelled.Cancel();
            try { await held; throw new Exception("Cancelled firewall operation completed"); }
            catch (OperationCanceledException) { }
            check(coordinator.Describe(ServerModules.FirewallId)!.ActiveLeases == 0
                && await coordinator.TrySleepAsync(ServerModules.FirewallId, CancellationToken.None) is null && !transport.Disposed,
                "cancelled firewall transport wait releases its facade lease without disposing Core resources");
            await app.StopAsync();
            await VerifyContainerFailureAsync(app.Services, context, check);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyContainerFailureAsync(IServiceProvider services, ServerModuleContext context, Action<bool, string> check)
    {
        var factory = new FailingContainerFactory();
        var ids = new HashSet<string>(StringComparer.Ordinal) { ServerModules.FirewallId };
        var options = ModuleResidencyOptions.Parse(new ConfigurationBuilder().Build(), ids, ids);
        await using var coordinator = new ModuleResidencyCoordinator([factory], options, services, context,
            services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>());
        using (var lease = await coordinator.AcquireAsync(ServerModules.FirewallId)) _ = lease.Runtime.Resolve(typeof(FailingResource));
        check(await coordinator.TrySleepAsync(ServerModules.FirewallId, CancellationToken.None) == "module-recovery-blocked"
            && factory.Failure.Disposals == 1 && factory.Other.Disposals == 1,
            "a real module container finishes other teardown but propagates resource failure to runtime quarantine");
        try { using var lease = await coordinator.AcquireAsync(ServerModules.FirewallId); throw new Exception("Failed container reloaded"); }
        catch (ModuleUnavailableException) { }
        await coordinator.DisposeAsync();
        check(factory.Created == 1 && factory.Failure.Disposals == 1 && factory.Other.Disposals == 1,
            "a failed module container cannot create another generation or retry partially completed resource disposal");
    }

    private sealed class FirewallTransport : IPrivilegedOperationTransport, IDisposable
    {
        internal int Calls;
        internal bool Disposed;
        internal PrivilegedOperationRequest? Last;
        internal TaskCompletionSource? Pending;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            if (Pending is { } pending) { Entered.TrySetResult(); await pending.Task.WaitAsync(cancellationToken); }
            var output = request.Operation == PrivilegedOperationKind.FirewallWindowsStatus
                ? JsonSerializer.SerializeToUtf8Bytes(new FirewallStatusDto(true, true, "windows-defender", null, "deny", "allow"))
                : Encoding.UTF8.GetBytes("Status: active\nDefault: deny (incoming), allow (outgoing)\n");
            return new(true, OutputBase64: Convert.ToBase64String(output));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FirewallMode(IConfiguration configuration) : IServerModeResolver
    {
        private readonly ServerModeResolver _inner = new(configuration, RelaxKonOS.Server.UserExecution.UserExecutionBackend.Disabled);
        internal bool Allowed = true;
        public ServerMode Mode => _inner.Mode;
        public ServerCapabilitiesDto Describe() => _inner.Describe();
        public bool Supports(ServerHostFeature feature) => Allowed;
    }
    private sealed class FirewallElevation : IHostElevationSessionStore
    {
        internal bool Allowed;
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target) => Allowed;
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target, bool includeDescendants, string authenticationMethod, string? correlationId = null) => throw new NotSupportedException();
        public void Revoke(ClaimsPrincipal principal) { }
    }
    private sealed class FailingResource : IDisposable
    { internal int Disposals; public void Dispose() { Disposals++; throw new IOException("injected resource release failure"); } }
    private sealed class OtherResource : IDisposable
    { internal int Disposals; public void Dispose() => Disposals++; }
    private sealed class FailingContainerFactory : IServerModuleRuntimeFactory
    {
        internal readonly FailingResource Failure = new(); internal readonly OtherResource Other = new(); internal int Created;
        public string ModuleId => ServerModules.FirewallId;
        public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
        { Created++; return ValueTask.FromResult<IServerModuleRuntime>(new FailingContainer(services, Failure, Other)); }
    }
    private sealed class FailingContainer : IServerModuleRuntime
    {
        private readonly ModuleServiceContainer _container;
        internal FailingContainer(IServiceProvider services, FailingResource failure, OtherResource other)
        {
            _container = new(ServerModules.FirewallId, services);
            _container.AddSingleton<OtherResource>(_ => other); _container.AddSingleton<FailingResource>(_ => failure);
            _ = _container.GetRequired<OtherResource>();
        }
        public string ModuleId => ServerModules.FirewallId;
        public object Resolve(Type serviceType) => _container.GetRequired(serviceType);
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync() => _container.DisposeAsync();
    }
}
