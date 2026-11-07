using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ProxyObservationLeaseChecks
{
    internal static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("ProxyRead", policy => policy.RequireRole("reader")));
        var ids = new HashSet<string> { ServerModules.ProxyId };
        var module = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([new ProxyWorkRuntimeFactory()],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services, module,
            services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(), residentWorkModuleIds: ids));
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource, CapturingLeaseSource>();
        var scenario = new Scenario(holdController: false);
        builder.Services.AddScoped<IProxyEngineRegistry>(_ => throw new InvalidOperationException("Connection reads must not create the management engine graph."));
        builder.Services.AddScoped(_ => new ControllerScope(scenario));
        builder.Services.AddScoped<IMihomoControllerClient>(services => services.GetRequiredService<ControllerScope>().CreateClient());
        await using var app = builder.Build();
        var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        app.Use(async (context, next) =>
        {
            var current = scenario;
            if (!current.HoldController)
                context.Response.OnStarting(async () =>
                {
                    current.ResponseEntered.TrySetResult();
                    await current.ReleaseResponse.Task.WaitAsync(ct);
                });
            using var registration = context.RequestAborted.Register(() => current.Disconnected.TrySetResult());
            try { await next(context); }
            finally { current.RequestExited.TrySetResult(); }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapProxyObservationEndpoints();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        scenario.ReleaseResponse.TrySetResult();
        using (var anonymous = await http.GetAsync(ProxyApiRoutes.Connections, ct))
            TestAssert.Assert(anonymous.StatusCode == HttpStatusCode.Unauthorized && !HasLease()
                && !scenario.ControllerEntered.Task.IsCompleted, "Anonymous reads must not start the Proxy graph or take a work lease.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        scenario = new Scenario(holdController: false) { Status = HttpStatusCode.Unauthorized };
        scenario.ReleaseResponse.TrySetResult();
        scenario.ReleaseCleanup.TrySetResult();
        using (var rejected = await http.GetAsync(ProxyApiRoutes.Connections, ct))
            TestAssert.Assert(rejected.StatusCode == HttpStatusCode.ServiceUnavailable
                && (await rejected.Content.ReadAsStringAsync(ct)).Contains(ProxyProblemCodes.ControllerAuthenticationFailed, StringComparison.Ordinal),
                "Controller authentication failure must remain an explicit HTTP 503.");
        await scenario.RequestExited.Task.WaitAsync(ct);
        TestAssert.Assert(!HasLease(), "The error response must drain its scope and lease.");

        foreach (var holdController in new[] { true, false })
        {
            scenario = new Scenario(holdController);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var request = http.GetAsync(ProxyApiRoutes.Connections, caller.Token);
            try
            {
                var barrier = holdController ? scenario.ControllerEntered : scenario.ResponseEntered;
                await barrier.Task.WaitAsync(ct);
                TestAssert.Assert(HasLease(), "Proxy must own the read and the actual response execution.");
                caller.Cancel();
                await scenario.Disconnected.Task.WaitAsync(ct);
                if (holdController) await scenario.ControllerCancelled.Task.WaitAsync(ct);
                try { using var unexpected = await request; throw new Exception("Expected client cancellation."); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                TestAssert.Assert(HasLease() && !scenario.RequestExited.Task.IsCompleted,
                    "Client cancellation must not return the lease while the real task is pending.");
                scenario.ReleaseController.TrySetResult();
                scenario.ReleaseResponse.TrySetResult();
                await scenario.CleanupEntered.Task.WaitAsync(ct);
                TestAssert.Assert(HasLease() && !scenario.RequestExited.Task.IsCompleted,
                    "A scope awaiting asynchronous disposal must retain the Proxy work lease.");
            }
            finally
            {
                scenario.ReleaseController.TrySetResult();
                scenario.ReleaseResponse.TrySetResult();
                scenario.ReleaseCleanup.TrySetResult();
            }
            await scenario.RequestExited.Task.WaitAsync(ct);
            TestAssert.Assert(!HasLease(), "A fully drained read must return its work lease.");
        }
        scenario = new Scenario(holdController: false) { FailCleanup = true };
        scenario.ReleaseResponse.TrySetResult();
        scenario.ReleaseCleanup.TrySetResult();
        var failed = http.GetAsync(ProxyApiRoutes.Connections, ct);
        var source = (CapturingLeaseSource)app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>();
        try
        {
            await scenario.RequestExited.Task.WaitAsync(ct);
            using var reply = await failed;
            TestAssert.Assert(reply.IsSuccessStatusCode && HasLease()
                && coordinator.Describe(ServerModules.ProxyId)?.BlockedBy == "module-recovery-blocked",
                "Cleanup failure must retain the work lease and block the original module owner.");
        }
        finally { source.Last?.Dispose(); }
        Console.WriteLine("PASS PROXY OBSERVATION HTTP DRAIN: authorization, controller cancellation, response, asynchronous cleanup and failure");

        bool HasLease() => coordinator.HasBackgroundWork(ServerModules.ProxyId);
    }

    private sealed class Scenario(bool holdController)
    {
        internal bool HoldController { get; } = holdController;
        internal bool FailCleanup { get; init; }
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal readonly TaskCompletionSource ControllerEntered = Signal();
        internal readonly TaskCompletionSource ControllerCancelled = Signal();
        internal readonly TaskCompletionSource ResponseEntered = Signal();
        internal readonly TaskCompletionSource Disconnected = Signal();
        internal readonly TaskCompletionSource CleanupEntered = Signal();
        internal readonly TaskCompletionSource RequestExited = Signal();
        internal readonly TaskCompletionSource ReleaseController = Signal();
        internal readonly TaskCompletionSource ReleaseResponse = Signal();
        internal readonly TaskCompletionSource ReleaseCleanup = Signal();

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControllerScope(Scenario scenario) : IAsyncDisposable
    {
        internal IMihomoControllerClient CreateClient() =>
            new MihomoControllerClient(new HttpClient(new ControllerTransport(scenario)), new StaticProxySecretStore(), new());

        public async ValueTask DisposeAsync()
        {
            scenario.CleanupEntered.TrySetResult();
            await scenario.ReleaseCleanup.Task;
            if (scenario.FailCleanup) throw new IOException("Unconfirmed fixture cleanup.");
        }
    }

    private sealed class CapturingLeaseSource(ModuleResidencyCoordinator coordinator) : IResidentModuleWorkLeaseSource
    {
        private readonly ResidentModuleWorkLeaseSource _source = new(coordinator);
        internal IModuleWorkLease? Last { get; private set; }

        public async ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default) =>
            Last = await _source.AcquireAsync(moduleId, ct);
    }

    private sealed class ControllerTransport(Scenario scenario) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var cancellation = ct.Register(() => scenario.ControllerCancelled.TrySetResult());
            scenario.ControllerEntered.TrySetResult();
            if (scenario.HoldController) await scenario.ReleaseController.Task;
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(scenario.Status) { Content = new StringContent("{\"connections\":[]}") };
        }
    }
}
