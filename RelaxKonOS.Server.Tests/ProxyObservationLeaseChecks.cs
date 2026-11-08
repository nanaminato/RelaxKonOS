using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Common;
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
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("ProxyRead", policy => policy.RequireRole("reader"));
            options.AddPolicy("ProxyManage", policy => policy.RequireRole("manager"));
        });
        var ids = new HashSet<string> { ServerModules.ProxyId };
        var module = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([new ProxyWorkRuntimeFactory()],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services, module,
            services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(), residentWorkModuleIds: ids));
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource, CapturingLeaseSource>();
        ProxyReadSampler<ProxyTrafficDto>.Register(builder.Services,
            static (services, ct) => services.GetRequiredService<IMihomoControllerClient>().GetTrafficAsync(ct));
        ProxyReadSampler<ProxyOverviewDto>.Register(builder.Services, static async (services, ct) =>
        {
            var reachable = await services.GetRequiredService<IMihomoControllerClient>().IsReachableAsync(ct);
            return new(MihomoEngine.Id, MihomoEngine.Capabilities, new(false, false, false, false, false, false, new(false, false, false, false)),
                new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, "1.19.10", null, true, false),
                new(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, reachable.Succeeded, true, true),
                ProxyOperatingMode.ListenerOnly, null, 1, new(false, false, null));
        });
        var scenario = new Scenario(holdController: false);
        builder.Services.AddScoped<IProxyEngineRegistry>(_ => throw new InvalidOperationException("Observation reads must not create the management engine graph."));
        builder.Services.AddSingleton<IProxyDiagnosticLogStore, FixtureDiagnostics>();
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-proxy-http-" + Guid.NewGuid().ToString("N"));
        var paths = new TestProxyPaths(root);
        builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
        builder.Services.AddSingleton<ProxyAuditStore>();
        var profile = new ProxyProfileDto(Guid.NewGuid(), "fixture", MihomoEngine.Id, true, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        builder.Services.AddSingleton<IProxyProfileRepository>(new FixtureProfiles(profile));
        builder.Services.AddScoped<IProxySettingsWriter>(_ => new FixtureWrite(scenario));
        builder.Services.AddScoped<IProxyConfigurationTransactionService>(_ => new FixtureWrite(scenario));
        builder.Services.AddScoped(_ => new ControllerScope(scenario));
        builder.Services.AddScoped<IMihomoControllerClient>(services => services.GetRequiredService<ControllerScope>().CreateClient());
        builder.Services.AddScoped<IProxyRuntimeManager, FixtureRuntime>();
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
        app.MapProxySettingsUpdateEndpoint();
        app.MapProxyProfilePublicationEndpoints();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        scenario.ReleaseResponse.TrySetResult();
        string[] routes = [ProxyApiRoutes.Connections, ProxyApiRoutes.Traffic, ProxyApiRoutes.Logs + "?limit=1",
            ProxyApiRoutes.Runtime, ProxyApiRoutes.Groups, ProxyApiRoutes.ControllerConfiguration, ProxyApiRoutes.Overview];
        foreach (var route in routes)
        {
            using var anonymous = await http.GetAsync(route, ct);
            TestAssert.Assert(anonymous.StatusCode == HttpStatusCode.Unauthorized && !HasLease()
                && !scenario.ControllerEntered.Task.IsCompleted, "Anonymous reads must not start the Proxy graph or take a work lease.");
        }
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

        scenario = new Scenario(holdController: false) { Status = HttpStatusCode.Unauthorized };
        scenario.ReleaseResponse.TrySetResult();
        scenario.ReleaseCleanup.TrySetResult();
        var groups = await http.GetFromJsonAsync<ProxyGroupDto[]>(ProxyApiRoutes.Groups, RelaxKonOSJsonOptions.Default, ct);
        await scenario.RequestExited.Task.WaitAsync(ct);
        TestAssert.Assert(groups is { Length: 0 } && !HasLease(), "An unauthorized controller must still return an empty group list.");

        foreach (var (status, mode) in new[] { (HttpStatusCode.OK, "direct"), (HttpStatusCode.OK, "invalid"), (HttpStatusCode.Unauthorized, "direct") })
        {
            scenario = new Scenario(holdController: false) { Status = status, RoutingMode = mode };
            scenario.ReleaseResponse.TrySetResult();
            scenario.ReleaseCleanup.TrySetResult();
            var reply = await http.GetFromJsonAsync<ProxyControllerConfigurationDto>(ProxyApiRoutes.ControllerConfiguration, RelaxKonOSJsonOptions.Default, ct);
            var expected = status == HttpStatusCode.OK
                ? new ProxyControllerConfigurationDto(mode == "direct" ? new(ProxyRoutingMode.Direct)
                    : new(ProxyRoutingMode.Rule, ProxyProblemCodes.ControllerResponseInvalid), new(true, false, "fake-ip"))
                : new ProxyControllerConfigurationDto(new(ProxyRoutingMode.Rule, ProxyProblemCodes.ControllerAuthenticationFailed),
                    new(false, false, null, ProxyProblemCodes.ControllerAuthenticationFailed));
            TestAssert.Assert(reply == expected && scenario.ControllerCalls == 1,
                "Routing and DNS must project the same single controller response, including authentication failure.");
            await scenario.RequestExited.Task.WaitAsync(ct);
            TestAssert.Assert(!HasLease(), "The failure response must drain its work lease.");
        }

        scenario = new Scenario(holdController: false) { Status = HttpStatusCode.Unauthorized };
        scenario.ReleaseResponse.TrySetResult();
        scenario.ReleaseCleanup.TrySetResult();
        using (var logs = await http.GetAsync(ProxyApiRoutes.Logs + "?limit=1", ct))
            TestAssert.Assert(logs.IsSuccessStatusCode
                && (await logs.Content.ReadAsStringAsync(ct)).Contains("fixture diagnostic", StringComparison.Ordinal),
                "A controller error must retain the bounded local diagnostics fallback.");
        await scenario.RequestExited.Task.WaitAsync(ct);

        foreach (var (route, holdController) in routes.SelectMany(route => new[] { (route, true), (route, false) }))
        {
            scenario = new Scenario(holdController);
            var cleanupBeforeResponse = (route == ProxyApiRoutes.Traffic || route == ProxyApiRoutes.Overview) && !holdController;
            if (cleanupBeforeResponse) scenario.ReleaseCleanup.TrySetResult();
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var request = http.GetAsync(route, caller.Token);
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
                if (!cleanupBeforeResponse)
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
        scenario = new Scenario(holdController: true);
        using (var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var first = http.GetAsync(ProxyApiRoutes.Overview, firstCaller.Token);
            await scenario.ControllerEntered.Task.WaitAsync(ct);
            var remaining = http.GetFromJsonAsync<ProxyOverviewDto>(ProxyApiRoutes.Overview, RelaxKonOSJsonOptions.Default, ct);
            try
            {
                while (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 3) await Task.Delay(10, ct);
                await firstCaller.CancelAsync();
                try { using var unexpected = await first; throw new Exception("Expected overview client cancellation."); }
                catch (OperationCanceledException) when (firstCaller.IsCancellationRequested) { }
                scenario.ReleaseController.TrySetResult();
                await scenario.CleanupEntered.Task.WaitAsync(ct);
                TestAssert.Assert(!scenario.ControllerCancelled.Task.IsCompleted && scenario.ControllerCalls == 1 && !remaining.IsCompleted
                    && coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases == 3,
                    "Overlapping overview reads must share execution and retain the response leases through async cleanup.");
            }
            finally
            {
                scenario.ReleaseController.TrySetResult();
                scenario.ReleaseCleanup.TrySetResult();
            }
            TestAssert.Assert(await remaining is { ActiveConnections: 1 }, "The other overview reader must receive the original sample.");
            while (HasLease()) await Task.Delay(10, ct);
        }
        scenario = new Scenario(holdController: true);
        using (var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var first = http.GetAsync(ProxyApiRoutes.Traffic, firstCaller.Token);
            await scenario.ControllerEntered.Task.WaitAsync(ct);
            var second = http.GetFromJsonAsync<ProxyTrafficDto>(ProxyApiRoutes.Traffic, RelaxKonOSJsonOptions.Default, ct);
            try
            {
                while (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 3) await Task.Delay(10, ct);
                await firstCaller.CancelAsync();
                await scenario.Disconnected.Task.WaitAsync(ct);
                try { using var unexpected = await first; throw new Exception("Expected traffic client cancellation."); }
                catch (OperationCanceledException) when (firstCaller.IsCancellationRequested) { }
                scenario.ReleaseController.TrySetResult();
                await scenario.CleanupEntered.Task.WaitAsync(ct);
                TestAssert.Assert(!scenario.ControllerCancelled.Task.IsCompleted && scenario.ControllerCalls == 2
                    && !second.IsCompleted && coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases == 3,
                    "The remaining traffic reader must share one native sample and keep its work through scope cleanup.");
            }
            finally
            {
                scenario.ReleaseController.TrySetResult();
                scenario.ReleaseCleanup.TrySetResult();
            }
            TestAssert.Assert(await second is { ProblemCode: "", UploadBytesPerSecond: 1, UploadTotalBytes: 3 },
                "The remaining traffic reader must receive the shared sample.");
            while (HasLease()) await Task.Delay(10, ct);
        }
        scenario = new Scenario(holdController: true);
        using (var lastCaller = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var last = http.GetAsync(ProxyApiRoutes.Traffic, lastCaller.Token);
            await scenario.ControllerEntered.Task.WaitAsync(ct);
            await lastCaller.CancelAsync();
            await scenario.ControllerCancelled.Task.WaitAsync(ct);
            try { using var unexpected = await last; throw new Exception("Expected last traffic client cancellation."); }
            catch (OperationCanceledException) when (lastCaller.IsCancellationRequested) { }
            scenario.ReleaseController.TrySetResult();
            await scenario.CleanupEntered.Task.WaitAsync(ct);
            var next = http.GetFromJsonAsync<ProxyTrafficDto>(ProxyApiRoutes.Traffic, RelaxKonOSJsonOptions.Default, ct);
            try
            {
                while (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 3) await Task.Delay(10, ct);
                TestAssert.Assert(scenario.ControllerCalls == 1 && !next.IsCompleted,
                    "A new reader must wait for the abandoned sample's controller scope to drain.");
            }
            finally { scenario.ReleaseCleanup.TrySetResult(); }
            TestAssert.Assert(await next is { ProblemCode: "", UploadTotalBytes: 3 } && scenario.ControllerCalls == 3,
                "The next reader must receive a fresh native sample after cancellation drains.");
            while (HasLease()) await Task.Delay(10, ct);
        }
        Console.WriteLine("PASS PROXY SHARED TRAFFIC: one sample for overlapping readers, last cancellation drains before a fresh read");
        var settingsPayload = new UpdateProxySettingsRequest(false, false, false, true, false, "warning", 7890);
        var profileRoute = ProxyApiRoutes.Proxy + ProxyApiRoutes.ProfilePattern.Replace("{profileId:guid}", profile.Id.ToString("D"), StringComparison.Ordinal);
        await VerifyWriteAsync("settings", token => http.PutAsJsonAsync(ProxyApiRoutes.Settings, settingsPayload, token), HttpStatusCode.NoContent);
        await VerifyWriteAsync("profile apply", token => http.PostAsJsonAsync(profileRoute + "/configuration/apply",
            new ApplyProxyConfigurationRequest("mode: direct\n"), token), HttpStatusCode.NoContent);
        await VerifyWriteAsync("profile activation", token => http.PostAsync(profileRoute + "/activate", null, token), HttpStatusCode.OK);
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
        Console.WriteLine("PASS PROXY OBSERVATION HTTP DRAIN: runtime, groups, controller configuration, connections, traffic and logs preserve authorization, cancellation, response and cleanup");

        bool HasLease() => coordinator.HasBackgroundWork(ServerModules.ProxyId);

        async Task VerifyWriteAsync(string workload, Func<CancellationToken, Task<HttpResponseMessage>> send, HttpStatusCode success)
        {
            scenario = new Scenario(holdController: true);
            using (var forbidden = await send(ct))
                TestAssert.Assert(forbidden.StatusCode == HttpStatusCode.Forbidden && !HasLease()
                    && !scenario.ControllerEntered.Task.IsCompleted, $"A reader cannot acquire {workload} execution.");
            http.DefaultRequestHeaders.Remove("X-Test-Role");
            http.DefaultRequestHeaders.Add("X-Test-Role", "manager");
            var audit = Path.Combine(paths.GetStateDirectory(), "proxy-audit.jsonl");
            try
            {
                foreach (var holdWriter in new[] { true, false })
                {
                    scenario = new Scenario(holdWriter);
                    if (!holdWriter) scenario.ReleaseController.TrySetResult();
                    using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var request = send(caller.Token);
                    try
                    {
                        await (holdWriter ? scenario.ControllerEntered : scenario.ResponseEntered).Task.WaitAsync(ct);
                        TestAssert.Assert(HasLease(), $"The {workload} task and response require a Proxy work lease.");
                        caller.Cancel();
                        await scenario.Disconnected.Task.WaitAsync(ct);
                        if (holdWriter) await scenario.ControllerCancelled.Task.WaitAsync(ct);
                        try { using var unexpected = await request; throw new Exception($"Expected {workload} client cancellation."); }
                        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                        TestAssert.Assert(HasLease() && !scenario.RequestExited.Task.IsCompleted,
                            $"A disconnected {workload} caller must leave the accepted work owned.");
                        scenario.ReleaseController.TrySetResult();
                        scenario.ReleaseResponse.TrySetResult();
                        await scenario.CleanupEntered.Task.WaitAsync(ct);
                        TestAssert.Assert(HasLease() && (await File.ReadAllLinesAsync(audit, ct)).Length == (holdWriter ? 1 : 2),
                            "The original audit must persist before returning the scope and work lease.");
                    }
                    finally
                    {
                        scenario.ReleaseController.TrySetResult();
                        scenario.ReleaseResponse.TrySetResult();
                        scenario.ReleaseCleanup.TrySetResult();
                    }
                    await scenario.RequestExited.Task.WaitAsync(ct);
                    TestAssert.Assert(!HasLease(), $"{workload} must return the work lease after asynchronous cleanup.");
                }
                foreach (var problem in new[] { null, ProxyProblemCodes.ConfigInvalid })
                {
                    scenario = new Scenario(holdController: false) { WriteProblem = problem };
                    scenario.ReleaseController.TrySetResult();
                    scenario.ReleaseResponse.TrySetResult();
                    scenario.ReleaseCleanup.TrySetResult();
                    using var reply = await send(ct);
                    await scenario.RequestExited.Task.WaitAsync(ct);
                    TestAssert.Assert(!HasLease() && reply.StatusCode == (problem is null ? success : HttpStatusCode.BadRequest)
                        && (problem is null || (await reply.Content.ReadAsStringAsync(ct)).Contains(problem, StringComparison.Ordinal)),
                        $"The scoped {workload} route must preserve the original success and validation responses.");
                    if (problem is null && success == HttpStatusCode.OK)
                        TestAssert.Assert(await reply.Content.ReadFromJsonAsync<ProxyProfileDto>(RelaxKonOSJsonOptions.Default, ct) == profile,
                            "Profile activation must return the original active profile response.");
                }
            }
            finally
            {
                http.DefaultRequestHeaders.Remove("X-Test-Role");
                http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
                if (File.Exists(audit)) File.Delete(audit);
                if (Directory.Exists(paths.GetStateDirectory())) Directory.Delete(paths.GetStateDirectory());
                if (Directory.Exists(root)) Directory.Delete(root);
            }
            Console.WriteLine($"PASS PROXY WRITE HTTP DRAIN: {workload}, authorization, accepted task, audit, actual response and asynchronous cleanup");
        }
    }

    private sealed class FixtureWrite(Scenario scenario) : IProxySettingsWriter, IProxyConfigurationTransactionService, IAsyncDisposable
    {
        public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken ct) => RunAsync(ct);
        public Task<string?> ApplyAsync(Guid profileId, string yaml, CancellationToken ct) => RunAsync(ct);
        public Task<string?> ActivateStoredAsync(Guid profileId, CancellationToken ct, ProxyQueuedSubscriptionRefresh? refresh = null,
            ProxyQueuedSubscriptionActivation? activation = null) => RunAsync(ct);
        public Task<string?> StoreAsync(Guid profileId, string yaml, CancellationToken ct, ProxyQueuedDraftRefresh? draft = null) => throw new NotSupportedException();
        public Task<string?> ReadStoredAsync(Guid profileId, CancellationToken ct) => throw new NotSupportedException();

        private async Task<string?> RunAsync(CancellationToken ct)
        {
            using var cancellation = ct.Register(() => scenario.ControllerCancelled.TrySetResult());
            scenario.ControllerEntered.TrySetResult();
            await scenario.ReleaseController.Task;
            return scenario.WriteProblem;
        }

        public async ValueTask DisposeAsync()
        {
            scenario.CleanupEntered.TrySetResult();
            await scenario.ReleaseCleanup.Task;
        }
    }

    private sealed class FixtureProfiles(ProxyProfileDto profile) : IProxyProfileRepository
    {
        public Task<ProxyProfileDto?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(id == profile.Id ? profile : null);
        public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProxyProfileDto?> GetActiveAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProxyProfileDto> UpsertAsync(Guid? id, string name, string engineId, long? expectedRevision, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProxyProfileDto?> SetActiveAsync(Guid id, CancellationToken ct, MihomoSubscriptionRefresh? refresh = null) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidatePublicationAsync(MihomoProfilePublication selection, CancellationToken ct) => throw new NotSupportedException();
        public Task CommitPublicationAsync(Guid operationId, MihomoProfilePublication selection, string identity, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Scenario(bool holdController)
    {
        internal bool HoldController { get; } = holdController;
        internal bool FailCleanup { get; init; }
        internal string? WriteProblem { get; init; }
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal string RoutingMode { get; init; } = "direct";
        internal int ControllerCalls;
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
            Interlocked.Increment(ref scenario.ControllerCalls);
            scenario.ControllerEntered.TrySetResult();
            if (scenario.HoldController) await scenario.ReleaseController.Task;
            ct.ThrowIfCancellationRequested();
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/traffic" => "{\"up\":1,\"down\":2}\n",
                "/logs" => "{\"type\":\"info\",\"payload\":\"fixture\"}\n",
                "/version" => "{\"version\":\"v1.19.10\"}",
                "/proxies" => "{\"proxies\":{}}",
                "/configs" => "{\"mode\":\"" + scenario.RoutingMode + "\",\"dns\":{\"enable\":true,\"enhanced-mode\":\"fake-ip\"}}",
                _ => "{\"uploadTotal\":3,\"downloadTotal\":4,\"connections\":[],\"memory\":5}"
            };
            return new HttpResponseMessage(scenario.Status) { Content = new StringContent(body) };
        }
    }

    private sealed class FixtureRuntime(IMihomoControllerClient controller) : IProxyRuntimeManager
    {
        public async Task<ProxyRuntimeDto> GetAsync(string engineId, CancellationToken ct)
        {
            var result = await controller.IsReachableAsync(ct);
            return new(engineId, ProxyRuntimeMode.Managed, result.Succeeded ? ProxyRuntimeState.Running : ProxyRuntimeState.Stopped,
                "1.19.10", null, true, false, result.ProblemCode);
        }

        public Task<ProxyRuntimeDto> DetectExternalAsync(string engineId, string executablePath, CancellationToken ct) =>
            throw new InvalidOperationException("Runtime status must not probe an external executable.");
    }

    private sealed class FixtureDiagnostics : IProxyDiagnosticLogStore
    {
        public Task WriteAsync(string level, string message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Observation must not write diagnostics.");
        public Task<IReadOnlyList<ProxyLogEntryDto>> ReadAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProxyLogEntryDto>>([new(DateTimeOffset.UnixEpoch, "warning", "fixture diagnostic")]);
    }
}
