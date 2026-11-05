using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Tailscale;

/// <summary>Real module graph and coordinator; only the external authenticated Helper port is controlled.</summary>
internal static class TailscaleBoundaryChecks
{
    private const string ModuleId = "relaxkonos.tailscale";
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string text)
        { if (!value) throw new Exception(text); Console.WriteLine($"PASS TAILSCALE BOUNDARY {++count}: {text}"); }
        var root = Directory.CreateTempSubdirectory("tailscale-boundary-").FullName;
        try
        {
            await using var f = new Fixture(root);
            var co = f.Coordinator;
            var kept = new List<Task>();
            Check(co.Describe(ModuleId) is { State: ModuleResidencyState.Dormant, ActiveLeases: 0 }
                && f.Transport.Calls == 0, "Resolving the three Host proxies does not construct or wake the Tailscale graph");
            var graph = await CaptureGraphAsync(co);
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = f.Transport.Block(PrivilegedOperationKind.TailscaleStatus);
                var pending = f.Manager.GetStatusAsync(cancellation.Token); kept.Add(pending);
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                Check(!pending.IsCompleted && co.Describe(ModuleId)!.ActiveLeases == 1
                    && await co.TrySleepAsync(ModuleId, default) == "module-active-work",
                    "Caller cancellation cannot detach a noncooperative actual status IPC Task or release its runtime");
                gate.Release.TrySetResult();
                try { await pending; throw new Exception("Canceled status was returned"); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Check(co.Describe(ModuleId)!.ActiveLeases == 0, "Status cancellation returns its real lease only after IPC completes");
            }
            await SleepAndCollectAsync(co, graph);
            Check(graph.All(reference => !reference.IsAlive), "Actual runtime, coordinators, journals and Helper bridges are collectable after release despite retained completed caller Tasks");

            for (var cycle = 0; cycle < 16; cycle++)
            {
                var status = f.Manager.GetStatusAsync(default); kept.Add(status); await status;
                graph = await CaptureGraphAsync(co);
                await SleepAndCollectAsync(co, graph);
            }
            Check(co.Describe(ModuleId) is { State: ModuleResidencyState.Dormant, ActiveLeases: 0 }
                && graph.All(reference => !reference.IsAlive), "Sixteen real graph wake/release cycles leave no graph or active lease behind; no ALC/code-unload claim");

            var controlRequest = new TailscalePrepareRequest(Guid.NewGuid(), f.Transport.Control.Revision, TailscaleAction.Connect);
            var preparedTask = f.Control.PrepareAsync(f.Principal, controlRequest, default); kept.Add(preparedTask);
            Check((await preparedTask).State == TailscaleOperationState.Prepared, "The production control journal durably prepares a frozen operation");
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = f.Transport.Block(PrivilegedOperationKind.TailscaleControlExecute);
                var execution = f.Control.ExecuteAsync(f.Principal, controlRequest.OperationId, true, cancellation.Token); kept.Add(execution);
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                Check(!execution.IsCompleted && !gate.Token.CanBeCanceled && co.Describe(ModuleId)!.ActiveLeases == 1
                    && await co.TrySleepAsync(ModuleId, default) == "module-active-work",
                    "Durable Applying transfers cancellation to the original Helper authority while the real method lease covers execution");
                gate.Release.TrySetResult();
                Check((await execution).State == TailscaleOperationState.Applied && f.Transport.Executions == 1,
                    "An accepted control mutation completes once after HTTP cancellation");
            }
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            Check((await f.Control.ExecuteAsync(f.Principal, controlRequest.OperationId, true, default)).State == TailscaleOperationState.Applied
                && f.Transport.Executions == 1, "A prepared plan and its own completed result survive object eviction without replay");

            var unknown = new TailscalePrepareRequest(Guid.NewGuid(), f.Transport.Control.Revision, TailscaleAction.Disconnect);
            await f.Control.PrepareAsync(f.Principal, unknown, default);
            f.Transport.DropControlReply = true; f.Transport.ExposeControlReceipt = false;
            Check((await f.Control.ExecuteAsync(f.Principal, unknown.OperationId, true, default)).State == TailscaleOperationState.Unknown,
                "A lost original response remains Unknown in the encrypted journal");
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            Check((await f.Control.GetAsync(f.Principal, unknown.OperationId, default)).State == TailscaleOperationState.Unknown
                && f.Transport.Executions == 2, "A later matching live host state cannot manufacture original success across generations");
            f.Transport.ExposeControlReceipt = true;
            Check((await f.Control.GetAsync(f.Principal, unknown.OperationId, default)).State == TailscaleOperationState.Applied
                && f.Transport.Executions == 2, "Only the external owner's original matching receipt resolves control Unknown, without a new execution");

            var loginRequest = new TailscaleLoginPrepareRequest(Guid.NewGuid());
            var loginPrepared = await f.Login.PrepareAsync(f.Principal, loginRequest, default);
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = f.Transport.Block(PrivilegedOperationKind.TailscaleLoginBegin);
                var begin = f.Login.ExecuteAsync(f.Principal, loginRequest.OperationId, true, cancellation.Token); kept.Add(begin);
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                Check(!begin.IsCompleted && !gate.Token.CanBeCanceled && co.Describe(ModuleId)!.ActiveLeases == 1,
                    "A first-login Begin holds its actual runtime through the original noncancelable accepted IPC");
                gate.Release.TrySetResult();
                Check((await begin).State == TailscaleLoginState.Issued && f.Transport.Begins == 1
                    && f.Transport.LastIntent is { } intent && intent.HostId == f.HostId && intent.SessionId == f.Session,
                    "The issued first-login operation preserves host and original session identity across graph generations");
            }
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            var reconnected = Principal(f.Actor, Guid.NewGuid());
            Check((await f.Login.GetAsync(reconnected, loginRequest.OperationId, default)).State == TailscaleLoginState.Issued
                && f.Transport.Begins == 1, "The same owner can observe an issued login after reconnect and eviction without rerunning login");
            try { await f.Login.ReadChallengeAsync(reconnected, loginRequest.OperationId, default); throw new Exception("New session received old challenge"); }
            catch (TailscaleControlException failure) when (failure.StatusCode == 404) { }
            Check(f.Transport.Challenges == 0, "A new session cannot consume the old session's one-time browser challenge");
            var challengeTask = f.Login.ReadChallengeAsync(f.Principal, loginRequest.OperationId, default); kept.Add(challengeTask);
            var challenge = await challengeTask;
            Check(TailscaleLoginUrl.IsOfficial(challenge.AuthenticationUrl) && f.Transport.Challenges == 1
                && !challenge.ToString().Contains(challenge.AuthenticationUrl, StringComparison.Ordinal),
                "The original session alone receives a redacted, one-time dedicated challenge response");
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            Check((await f.Login.ExecuteAsync(f.Principal, loginRequest.OperationId, true, default)).State == TailscaleLoginState.Issued
                && f.Transport.Begins == 1 && f.Transport.Challenges == 1,
                "A retained challenge Task and issued durable login do not root the released graph or replay challenge/native Begin");
            var recovery = await f.Login.ReadRecoveryAsync(reconnected, loginRequest.OperationId, default);
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = f.Transport.Block(PrivilegedOperationKind.TailscaleLoginClose);
                var closing = f.Login.CloseAsync(reconnected, new(loginRequest.OperationId, recovery.Observation!.Revision, true), cancellation.Token);
                kept.Add(closing); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                Check(!closing.IsCompleted && !gate.Token.CanBeCanceled && co.Describe(ModuleId)!.ActiveLeases == 1,
                    "A durable login ClosingRequest retains real execution protection and its original intent after reconnect");
                gate.Release.TrySetResult();
                Check((await closing).Closure is not null && f.Transport.Closes == 1, "Manual close records observation without inventing native login success");
            }
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            Check((await f.Login.GetAsync(reconnected, loginRequest.OperationId, default)).Closure is not null
                && f.Transport.Closes == 1 && f.Transport.Begins == 1, "The original manual closure survives eviction without resubmitting Close or Begin");
            var uncertainLogin = new TailscaleLoginPrepareRequest(Guid.NewGuid());
            await f.Login.PrepareAsync(f.Principal, uncertainLogin, default);
            f.Transport.DropLoginReply = true; f.Transport.ExposeLoginReceipt = false;
            Check((await f.Login.ExecuteAsync(f.Principal, uncertainLogin.OperationId, true, default)).State == TailscaleLoginState.Unknown
                && f.Transport.Begins == 2, "A dropped first-login response records Unknown while preserving the original external intent and receipt");
            graph = await CaptureGraphAsync(co); await SleepAndCollectAsync(co, graph);
            f.Transport.LoginObservation = f.Transport.LoginObservation with { BackendState = "Running", CanStart = false };
            Check((await f.Login.GetAsync(reconnected, uncertainLogin.OperationId, default)).State == TailscaleLoginState.Unknown
                && f.Transport.Begins == 2, "A later Running daemon state does not replace a missing original login receipt after eviction");
            f.Transport.ExposeLoginReceipt = true;
            Check((await f.Login.GetAsync(reconnected, uncertainLogin.OperationId, default)).State == TailscaleLoginState.Issued
                && f.Transport.Begins == 2, "Only the original login receipt resolves Unknown; later daemon state never upgrades Issued to Completed or replays Begin");
            f.Grants.Allowed = false;
            var calls = f.Transport.Calls;
            try { await f.Control.PrepareAsync(f.Principal, new(Guid.NewGuid(), f.Transport.Control.Revision, TailscaleAction.Connect), default); throw new Exception("Grant bypass"); }
            catch (TailscaleControlException failure) when (failure.StatusCode == 403) { }
            Check(f.Transport.Calls == calls && f.Grants.Created == f.Grants.Disposed,
                "Fresh scoped grant checks are disposed and denied work reaches no external Helper action");

            await VerifyFailedJournalOpenAsync(root, f.App.Services.GetRequiredService<IDataProtectionProvider>(), Check);
            GC.KeepAlive(kept); GC.KeepAlive(challenge);
            Console.WriteLine($"Tailscale boundary checks passed: {count}");
        }
        finally { Directory.Delete(root, true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CaptureGraphAsync(ModuleResidencyCoordinator coordinator)
    {
        using var lease = await coordinator.AcquireAsync(ModuleId);
        var runtime = lease.Runtime;
        return new[] { runtime, runtime.Resolve(typeof(ITailscaleManager)), runtime.Resolve(typeof(ITailscaleControl)),
            runtime.Resolve(typeof(ITailscaleLogin)), runtime.Resolve(typeof(TailscaleOperationJournal)), runtime.Resolve(typeof(TailscaleLoginJournal)),
            runtime.Resolve(typeof(ITailscaleControlHost)), runtime.Resolve(typeof(ITailscaleLoginHost)), runtime.Resolve(typeof(ITailscaleControlAuthorization)) }
            .Select(instance => new WeakReference(instance)).ToArray();
    }
    private static async Task SleepAndCollectAsync(ModuleResidencyCoordinator coordinator, WeakReference[] graph)
    {
        if (await coordinator.TrySleepAsync(ModuleId, default) is not null
            || coordinator.Describe(ModuleId) is not { State: ModuleResidencyState.Dormant, ActiveLeases: 0 })
            throw new Exception("The fully finished Tailscale object graph did not release.");
        // Collection is a test oracle for object roots, never a production memory-control action.
        for (var i = 0; i < 8 && graph.Any(reference => reference.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(20); }
        if (graph.Any(reference => reference.IsAlive)) throw new Exception("A released Tailscale business graph remains rooted.");
    }
    private static void AssertNoDatabaseHandle(string database)
    {
        if (OperatingSystem.IsLinux())
            foreach (var descriptor in Directory.EnumerateFiles("/proc/self/fd"))
            {
                string? target;
                try { target = new FileInfo(descriptor).LinkTarget; }
                catch (IOException) when (!File.Exists(descriptor)) { continue; }
                if (target == database) throw new Exception("A failed journal Open retained a native database descriptor.");
            }
        // On Windows this is an OS share-mode proof; on Linux only the /proc check above
        // observes SQLite's native POSIX open, which is independent of .NET FileShare locks.
        using var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static Task VerifyFailedJournalOpenAsync(string root, IDataProtectionProvider protection, Action<bool, string> check)
    {
        foreach (var login in new[] { false, true })
        {
            var directory = Path.Combine(root, "corrupt-" + login); Directory.CreateDirectory(directory);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory }); builder.Logging.ClearProviders();
            var data = Path.Combine(directory, "data", login ? "tailscale-login" : "tailscale-operations"); Directory.CreateDirectory(data);
            var database = Path.Combine(data, "operations.db"); var bytes = Enumerable.Repeat((byte)0xff, 4096).ToArray(); File.WriteAllBytes(database, bytes);
            for (var attempt = 0; attempt < 16; attempt++)
            {
                try
                {
                    if (login) _ = new TailscaleLoginJournal(builder.Environment, protection);
                    else _ = new TailscaleOperationJournal(builder.Environment, protection);
                    throw new Exception("Corrupt journal was silently reset.");
                }
                catch (SqliteException) { }
                // Deliberately no GC between failure and native-handle check.
                AssertNoDatabaseHandle(database);
            }
            check(File.ReadAllBytes(database).SequenceEqual(bytes), $"{(login ? "Login" : "Control")} journal repeated corrupt Open failures dispose unpublished native connections immediately and preserve the original corrupt file");
        }
        return Task.CompletedTask;
    }
    private static ClaimsPrincipal Principal(Guid actor, Guid session) => new(new ClaimsIdentity([new("sub", actor.ToString()), new("sid", session.ToString())], "test"));
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly WebApplication App;
        internal readonly Transport Transport = new();
        internal readonly GrantState Grants = new();
        internal readonly Guid Actor = Guid.NewGuid(), Session = Guid.NewGuid();
        internal ClaimsPrincipal Principal => TailscaleBoundaryChecks.Principal(Actor, Session);
        internal ModuleResidencyCoordinator Coordinator => App.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal ITailscaleManager Manager { get; }
        internal ITailscaleControl Control { get; }
        internal ITailscaleLogin Login { get; }
        internal Guid HostId => App.Services.GetRequiredService<ServerRuntimeSettingsStore>().Current.InstanceId;
        internal Fixture(string root)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root }); builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ModuleResidency:IdleGraceSeconds"] = "0" });
            var keys = Directory.CreateDirectory(Path.Combine(root, "keys"));
            builder.Services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(keys));
            builder.Services.AddSingleton(new ServerRuntimeSettingsStore(Path.Combine(root, "settings.json")));
            builder.Services.AddSingleton<IPrivilegedOperationTransport>(Transport);
            builder.Services.AddScoped<IHostElevationSessionStore>(_ => new ScopedGrants(Grants));
            var modules = ServerModules.CreateManaged(new HashSet<string> { ModuleId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            App = builder.Build(); Manager = App.Services.GetRequiredService<ITailscaleManager>(); Control = App.Services.GetRequiredService<ITailscaleControl>(); Login = App.Services.GetRequiredService<ITailscaleLogin>();
        }
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
    private sealed class GrantState { internal bool Allowed = true; internal int Created, Disposed; }
    private sealed class ScopedGrants : IHostElevationSessionStore, IDisposable
    {
        private readonly GrantState _state;
        internal ScopedGrants(GrantState state) { _state = state; _state.Created++; }
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target) =>
            _state.Allowed && principal.Identity?.IsAuthenticated == true && capability == HostElevationCapability.HostNetworkChange && target == TailscaleAccess.Resource;
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target, bool descendants, string method, string? correlation = null) => throw new NotSupportedException();
        public void Revoke(ClaimsPrincipal principal) => _state.Allowed = false;
        public void Dispose() => _state.Disposed++;
    }
    private sealed class Gate(PrivilegedOperationKind operation)
    {
        internal readonly PrivilegedOperationKind Operation = operation;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal TailscaleControlSnapshot Control = new(new('A', 64), false, true, new(true, false), true);
        internal TailscaleLoginObservation LoginObservation = new(new('C', 64), new('D', 64), "NeedsLogin", true);
        private readonly Dictionary<Guid, TailscaleControlResult> _controlReceipts = [];
        private readonly Dictionary<Guid, TailscaleLoginReceipt> _loginReceipts = [];
        private readonly Dictionary<Guid, TailscaleLoginClosure> _closures = [];
        private Gate? _gate;
        internal int Calls, Executions, Begins, Challenges, Closes;
        internal bool DropControlReply, ExposeControlReceipt = true, DropLoginReply, ExposeLoginReceipt = true;
        internal TailscaleLoginIntent? LastIntent;
        internal Gate Block(PrivilegedOperationKind operation) => _gate = new(operation);
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.OperationId is not { } operationId || operationId == Guid.Empty)
                throw new InvalidDataException("Every controlled Helper call requires a nonempty original operation identity.");
            Calls++;
            if (_gate is { } gate && gate.Operation == request.Operation)
            { _gate = null; gate.Token = cancellationToken; gate.Entered.TrySetResult(); await gate.Release.Task; }
            switch (request.Operation)
            {
                case PrivilegedOperationKind.TailscaleStatus: return new(true, TailscaleStatus: new("observed"));
                case PrivilegedOperationKind.TailscaleControlRead: return new(true, TailscaleControl: Control);
                case PrivilegedOperationKind.TailscaleControlExecute:
                    Executions++; Control = Control with { Connected = request.TailscaleChange!.Action == TailscaleAction.Connect, Revision = new(Executions == 1 ? 'B' : 'E', 64) };
                    var receipt = new TailscaleControlResult(operationId, true, Control); _controlReceipts[operationId] = receipt;
                    if (DropControlReply) { DropControlReply = false; return new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable); }
                    return new(true, TailscaleResult: receipt);
                case PrivilegedOperationKind.TailscaleControlReceipt: return new(true, TailscaleResult: ExposeControlReceipt ? _controlReceipts.GetValueOrDefault(operationId) : null);
                case PrivilegedOperationKind.TailscaleRecoveryReceipt: return new(true);
                case PrivilegedOperationKind.TailscaleLoginObserve: return new(true, TailscaleLoginObservation: LoginObservation);
                case PrivilegedOperationKind.TailscaleLoginBegin:
                    Begins++; LastIntent = request.TailscaleLogin!.Intent;
                    var issued = new TailscaleLoginReceipt(operationId, TailscaleLoginState.Issued, DateTimeOffset.UtcNow, LoginObservation.PreferencesRevision, LoginObservation.Revision, new('F', 64));
                    _loginReceipts[operationId] = issued;
                    if (DropLoginReply) { DropLoginReply = false; return new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable); }
                    return new(true, TailscaleLoginReceipt: issued);
                case PrivilegedOperationKind.TailscaleLoginReceipt: return new(true, TailscaleLoginReceipt: ExposeLoginReceipt ? _loginReceipts.GetValueOrDefault(operationId) : null);
                case PrivilegedOperationKind.TailscaleLoginChallenge:
                    Challenges++; if (Challenges != 1) return new(false, ProblemCode: PrivilegedProblemCode.Conflict);
                    return new(true, TailscaleLoginChallenge: new(operationId, "https://login.tailscale.com/a/testChallenge123", LoginObservation));
                case PrivilegedOperationKind.TailscaleLoginRecoveryRead: return new(true, TailscaleLoginRecovery: new(operationId, LoginObservation, true));
                case PrivilegedOperationKind.TailscaleLoginClose:
                    Closes++; var closure = new TailscaleLoginClosure(request.TailscaleLoginIdentity!, LoginObservation, DateTimeOffset.UtcNow); _closures[operationId] = closure;
                    return new(true, TailscaleLoginClosure: closure);
                case PrivilegedOperationKind.TailscaleLoginRecoveryReceipt: return new(true, TailscaleLoginClosure: _closures.GetValueOrDefault(operationId));
                default: throw new InvalidOperationException("Unexpected external Helper verb " + request.Operation);
            }
        }
    }
}
