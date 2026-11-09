using Microsoft.AspNetCore.Hosting;

internal static class ProxySubscriptionBoundaryChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine($"PASS SUBSCRIPTION BOUNDARY {++count}: {message}"); }
        await CheckHttpAsync(Check);
        foreach (var kind in Enum.GetValues<ProxySubscriptionOperation>())
        {
            await using var f = new Fixture();
            var id = Guid.NewGuid();
            using var request = new CancellationTokenSource();
            Check(f.State.Created == 0, "The root provider does not construct the subscription execution graph");
            f.Operation = (await f.EnqueueAsync("once", kind, id, "actor", request.Token)).OperationId;
            request.Cancel();
            await f.State.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.State.Created == 1 && f.State.Disposed == 0 && !f.State.Token.CanBeCanceled
                && f.State.Kind == kind && (kind == ProxySubscriptionOperation.RefreshAll || f.State.Id == id),
                "Accepted work owns its service and survives request disposal/cancellation: " + kind);
            for (var i = 0; i < 10 && f.Acceptance!.IsAlive; i++)
            { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Check(!f.Acceptance!.IsAlive, "In-flight subscription work does not retain the request acceptance object");
            var replay = await f.EnqueueAsync("once", kind, id, "actor", default);
            Check(replay.OperationId == f.Operation && f.State.Created == 1, "Idempotent replay neither constructs nor executes another scope");
            f.State.Release.TrySetResult();
            await f.State.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = f.Store.StopAsync(default);
            Check(!stop.IsCompleted && (await f.Store.GetAsync(f.Operation, default))!.State == ProxyOperationState.Running
                && File.ReadAllText(f.Audit).Contains("succeeded", StringComparison.Ordinal),
                "Audit completes but terminal success and shutdown wait for asynchronous service disposal");
            try { await f.EnqueueAsync("late", kind, id, "actor", default); throw new Exception("Stopping store admitted work"); }
            catch (ProxyOperationValidationException) { }
            f.State.ReleaseDisposal.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.State.Disposed == 1 && (await f.Store.GetAsync(f.Operation, default))!.State == ProxyOperationState.Succeeded,
                "Shutdown drains disposal and terminal persistence without cancelling accepted work");
            for (var i = 0; i < 10 && f.State.Reference!.IsAlive; i++)
            { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Check(!f.State.Reference!.IsAlive, "Retaining the queue, completed request and ledger does not retain the completed subscription instance");
        }
        foreach (var failure in new[] { "domain", "exception", "audit", "dispose" })
        {
            await using var f = new Fixture(); f.State.Failure = failure;
            if (failure == "audit") Directory.CreateDirectory(f.Audit);
            var accepted = await f.EnqueueAsync("fail", ProxySubscriptionOperation.Refresh, Guid.NewGuid(), "actor", default);
            f.State.Release.TrySetResult(); f.State.ReleaseDisposal.TrySetResult();
            try { await f.Store.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ProxyOperationCleanupException) when (failure == "dispose") { }
            var item = await f.Store.GetAsync(accepted.OperationId, default);
            Check(item!.State == ProxyOperationState.Failed && f.State.Disposed == 1,
                "Failure is terminal only after scope cleanup and never silently succeeds: " + failure);
            if (failure == "dispose")
            {
                try { await f.EnqueueAsync("after-cleanup-failure", ProxySubscriptionOperation.RefreshAll, Guid.Empty, "actor", default); throw new Exception("Failed cleanup owner accepted more work"); }
                catch (ProxyOperationValidationException) { Check((await f.Store.InspectResponsibilitiesAsync(default)).Count != 0, "Failed cleanup retains the scope and blocks new execution rather than retrying partial disposal"); }
            }
            if (failure is "domain" or "exception")
                Check(File.ReadAllText(f.Audit).Contains("failed", StringComparison.Ordinal), "Domain and thrown failures preserve failure audit: " + failure);
        }
        await using (var f = new Fixture())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await f.Store.StopAsync(cancelled.Token); } catch (OperationCanceledException) { }
            try { await f.EnqueueAsync("after-expired-stop", ProxySubscriptionOperation.RefreshAll, Guid.Empty, "actor", default); throw new Exception("Expired shutdown budget left admission open"); }
            catch (ProxyOperationValidationException) { Check(f.State.Created == 0, "Even an already-cancelled shutdown wait closes new admission without constructing work"); }
        }
        await using (var f = new Fixture())
        {
            var accepted = await f.EnqueueAsync("timeout", ProxySubscriptionOperation.Activate, Guid.NewGuid(), "actor", default);
            await f.State.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource();
            var stop = f.Store.StopAsync(timeout.Token); timeout.Cancel();
            try { await stop; throw new Exception("Cancelled shutdown wait succeeded"); } catch (OperationCanceledException) { }
            Check(f.State.Disposed == 0 && !f.State.Token.IsCancellationRequested,
                "A cancelled shutdown wait does not cancel or dispose an active subscription operation");
            var hostedStop = ((Microsoft.Extensions.Hosting.IHostedService)f.Store).StopAsync(timeout.Token);
            Check(!hostedStop.IsCompleted, "Host shutdown continues draining after its deadline expires");
            f.State.Release.TrySetResult();
            await f.State.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!hostedStop.IsCompleted && (await f.Store.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Running,
                "Host shutdown cannot finish during asynchronous cleanup or report a premature terminal result");
            f.State.ReleaseDisposal.TrySetResult();
            await hostedStop.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Store.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Succeeded,
                "The accepted operation can finish after a shutdown waiter gives up");
        }
        await using (var f = new Fixture())
        {
            f.State.Failure = "dispose";
            var accepted = await f.EnqueueAsync("persist", ProxySubscriptionOperation.RefreshAll, Guid.Empty, "actor", default);
            await f.State.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(Path.Combine(f.Paths.GetStateDirectory(), "proxy-operations.json.new"));
            f.State.Release.TrySetResult(); f.State.ReleaseDisposal.TrySetResult();
            try { await f.Store.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Lost terminal persistence was hidden"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(f.State.Disposed == 1 && f.State.Reference!.IsAlive,
                "Combined disposal and terminal persistence failures retain the unresolved business scope");
            try { await f.EnqueueAsync("after-loss", ProxySubscriptionOperation.RefreshAll, Guid.Empty, "actor", default); throw new Exception("Unconfirmed owner accepted more work"); }
            catch (ProxyOperationValidationException) { count++; }
            var reopened = new ProxyOperationStore(f.Paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            var ledger = Path.Combine(f.Paths.GetStateDirectory(), "proxy-operations.json");
            var unchanged = await File.ReadAllTextAsync(ledger);
            try { await reopened.GetAsync(accepted.OperationId, default); throw new Exception("Unwritable recovery returned a durable receipt"); }
            catch (RelaxKonOS.Server.Residency.ModuleUnavailableException e) when (e.Code == "module-recovery-blocked")
            {
                Check(await File.ReadAllTextAsync(ledger) == unchanged, "Failed receipt persistence leaves the original batch ledger intact");
            }
            Directory.Delete(ledger + ".new");
            Check((await reopened.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Interrupted
                && await File.ReadAllTextAsync(ledger) == unchanged,
                "A quarantined recovery owner leaves the original ledger unchanged after storage becomes writable");
            var restarted = new ProxyOperationStore(f.Paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            Check((await restarted.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Interrupted
                && await File.ReadAllTextAsync(ledger) != unchanged,
                "A fresh recovery owner reports interruption without replaying or inferring publication success");
        }
        Console.WriteLine($"Subscription background boundary checks passed: {count}");
    }

    private static async Task CheckHttpAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "proxy-subscription-http-" + Guid.NewGuid().ToString("N"));
        var state = new State();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("boundary").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Authentication>("boundary", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("ProxyManage", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IProxyPlatformPaths>(new TestProxyPaths(root));
        builder.Services.AddSingleton<IProxyWorkLeaseSource, TestProxyWorkLeases>(); builder.Services.AddSingleton<ProxyOperationStore>(); builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Residency.IResidentModuleWorkLeaseSource, TestResidentBackgroundLeases>();
        builder.Services.AddScoped<ProxySubscriptionOperations>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddScoped<IProxySubscriptionService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IProxySubscriptionActions>(_ => new Subscription(state));
        await using var app = builder.Build();
        Microsoft.AspNetCore.Builder.AuthAppBuilderExtensions.UseAuthentication(app);
        Microsoft.AspNetCore.Builder.AuthorizationAppBuilderExtensions.UseAuthorization(app);
        RelaxKonOS.Server.Endpoints.ProxyEndpoints.MapSubscriptionOperationEndpoints(app);
        try
        {
            await app.StartAsync();
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var address = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var denied = await client.PostAsync(ProxyApiRoutes.SubscriptionsRefresh, null);
            check(denied.StatusCode == HttpStatusCode.Unauthorized && state.Created == 0,
                "The production subscription route rejects unauthenticated requests without constructing a service");
            client.DefaultRequestHeaders.Add("X-Boundary-Actor", "http-actor");
            using var missing = await client.PostAsync(ProxyApiRoutes.SubscriptionsRefresh, null);
            check(missing.StatusCode == HttpStatusCode.BadRequest && state.Created == 0,
                "Missing idempotency key is rejected before allocating a business scope");
            client.DefaultRequestHeaders.Add("Idempotency-Key", "http-once");
            using var accepted = await client.PostAsync(ProxyApiRoutes.SubscriptionsRefresh, null);
            check(accepted.StatusCode == HttpStatusCode.Accepted, "The production route retains HTTP 202 acceptance");
            await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var replay = await client.PostAsync(ProxyApiRoutes.SubscriptionsRefresh, null);
            check(replay.StatusCode == HttpStatusCode.Accepted && state.Created == 1,
                "A completed HTTP request and its replay leave exactly one independent task scope");
            var stop = app.StopAsync();
            state.Release.TrySetResult();
            await state.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!stop.IsCompleted, "The actual host waits for subscription asynchronous cleanup on shutdown");
            state.ReleaseDisposal.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            var audit = File.ReadAllText(Path.Combine(new TestProxyPaths(root).GetStateDirectory(), "proxy-audit.jsonl"));
            check(audit.Contains("http-actor", StringComparison.Ordinal) && audit.Contains("subscription.refresh_all", StringComparison.Ordinal),
                "The background audit retains the actor value after the HTTP context ends");
        }
        finally
        {
            state.Release.TrySetResult(); state.ReleaseDisposal.TrySetResult();
            await app.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class Authentication(IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers["X-Boundary-Actor"].ToString();
            if (actor.Length == 0) return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], Scheme.Name));
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new(principal, Scheme.Name)));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "proxy-subscription-boundary-" + Guid.NewGuid().ToString("N"));
        public TestProxyPaths Paths { get; }
        public ServiceProvider Provider { get; }
        public State State { get; } = new();
        public WeakReference? Acceptance { get; private set; }
        public ProxyOperationStore Store => Provider.GetRequiredService<ProxyOperationStore>();
        public Guid Operation;
        public string Audit => Path.Combine(Paths.GetStateDirectory(), "proxy-audit.jsonl");
        public Fixture()
        {
            Paths = new(root); Directory.CreateDirectory(Paths.GetStateDirectory());
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IProxyPlatformPaths>(Paths);
            services.AddSingleton<IProxyWorkLeaseSource, TestProxyWorkLeases>(); services.AddSingleton<ProxyOperationStore>(); services.AddSingleton<ProxyAuditStore>(); services.AddScoped<ProxySubscriptionOperations>();
            services.AddScoped<IProxySubscriptionActions>(_ => new Subscription(State));
            Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public async Task<ProxyOperationDto> EnqueueAsync(string key, ProxySubscriptionOperation kind, Guid id, string actor, CancellationToken ct)
        {
            await using var scope = Provider.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<ProxySubscriptionOperations>();
            Acceptance = new(queue);
            return await queue.EnqueueAsync(key, kind, id, actor, ct);
        }
        public async ValueTask DisposeAsync()
        {
            State.Release.TrySetResult(); State.ReleaseDisposal.TrySetResult();
            await Provider.DisposeAsync(); Directory.Delete(root, true);
        }
    }
    private sealed class State
    {
        public int Created, Disposed;
        public string Failure = "";
        public WeakReference? Reference;
        public Guid Id;
        public Guid BatchSubscriptionId { get; } = Guid.NewGuid();
        public ProxySubscriptionOperation Kind;
        public CancellationToken Token;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Subscription : IProxySubscriptionActions, IAsyncDisposable
    {
        private readonly State state;
        public Subscription(State state) { this.state = state; Interlocked.Increment(ref state.Created); state.Reference = new(this); }
        private async Task<string?> Run(ProxySubscriptionOperation kind, Guid id, CancellationToken ct)
        {
            state.Kind = kind; state.Id = id; state.Token = ct; state.Started.TrySetResult(); await state.Release.Task;
            if (state.Failure == "exception") throw new IOException("Controlled subscription failure");
            return state.Failure == "domain" ? ProxyProblemCodes.SubscriptionInvalid : null;
        }
        public Task<ProxySubscriptionTarget?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<ProxySubscriptionTarget?>(new(id, false, (_, _) => Task.FromResult("test-content")));
        public Task<string?> StoreAsync(Guid profileId, string content, ProxySubscriptionRefresh? refresh, CancellationToken ct) =>
            Run(profileId == state.BatchSubscriptionId ? ProxySubscriptionOperation.RefreshAll : ProxySubscriptionOperation.Refresh, profileId, ct);
        public Task<string?> ActivateAsync(Guid profileId, Guid id, Guid operationId, CancellationToken ct) =>
            Run(ProxySubscriptionOperation.Activate, id, ct);
        public Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([state.BatchSubscriptionId]);
        public Task<string?> PublishRefreshAsync(Guid profileId, string content, ProxySubscriptionRefresh refresh, CancellationToken ct) =>
            throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            state.Disposing.TrySetResult(); await state.ReleaseDisposal.Task; Interlocked.Increment(ref state.Disposed);
            if (state.Failure == "dispose") throw new IOException("Controlled disposal failure");
        }
    }
}
