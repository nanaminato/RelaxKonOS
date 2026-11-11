using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class FirewallBoundaryChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool success, string message)
        { if (!success) throw new InvalidOperationException(message); Console.WriteLine($"PASS FIREWALL BOUNDARY {++count}: {message}"); }
        Check(!typeof(ServerModules).Assembly.GetReferencedAssemblies().Any(name => name.Name == "RelaxKonOS.ServerModule.Firewall"),
            "Host does not statically reference the Firewall execution assembly");
        Check(typeof(IHostFirewallService).Assembly.GetName().Name == "RelaxKonOS.ServerModule.Firewall.Contracts"
            && !typeof(IHostFirewallService).Assembly.GetReferencedAssemblies().Any(name => name.Name is "RelaxKonOS.Server" or "RelaxKonOS.ServerModule.Firewall"),
            "Firewall service, exceptions and narrow ports belong to independent shared contracts");
        Check(!typeof(FirewallHostRequest).GetProperties().Any(property => property.PropertyType == typeof(PrivilegedOperationRequest)
                || property.Name is "Path" or "ServiceId" or "PackageChange"),
            "The module Host port cannot express non-Firewall privileged data");
        var retained = new List<object>();
        foreach (var platform in new[] { HostPlatformKind.Linux, HostPlatformKind.Windows })
        {
            await using var fixture = new Fixture(platform);
            var factory = fixture.Factory; var coordinator = fixture.Coordinator; var service = fixture.Service; var transport = fixture.Transport;
            Check(factory.LastCodeReference is null && coordinator.Describe(ServerModules.FirewallId)!.Generation == 0,
                $"{platform}: dormant inventory does not load execution code");
            var code = await RunRetainedVectorsAsync(fixture, retained);
            Check(code.All(reference => !reference.IsAlive),
                $"{platform}: 100 actual execution contexts collect while callers retain completed/failed/cancelled tasks, DTOs and sanitized exceptions");
            Check(retained.OfType<Exception>().All(error => error.InnerException is null
                    && !(error.StackTrace ?? "").Contains("LinuxUfwFirewallService", StringComparison.Ordinal)
                    && !(error.StackTrace ?? "").Contains("WindowsFirewallService", StringComparison.Ordinal)),
                $"{platform}: shared exception snapshots contain no original private stack or inner exception");
            Check(!transport.Disposed && transport.Calls == 100,
                $"{platform}: each retained vector executes once and never disposes the borrowed Core Helper");

            transport.Mode = Mode.Pending;
            using var cancellation = new CancellationTokenSource();
            var pending = service.SetEnabledAsync(true, cancellation.Token);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var protectedCode = factory.LastCodeReference!;
            cancellation.Cancel();
            Check(!pending.IsCompleted && coordinator.Describe(ServerModules.FirewallId)!.ActiveLeases == 1
                && await coordinator.TrySleepAsync(ServerModules.FirewallId, default) == "module-active-work",
                $"{platform}: request cancellation cannot detach non-cooperative Helper work or release execution code");
            Collect();
            Check(protectedCode.IsAlive, $"{platform}: actual in-flight mutation retains its execution generation");
            var before = transport.Calls;
            transport.Pending!.SetResult(new(false, ProblemCode: PrivilegedProblemCode.RecoveryRequired));
            var unknown = await pending;
            retained.Add(pending); retained.Add(unknown);
            Check(!unknown.Success && unknown.ProblemCode == "firewall.operation_failed" && transport.Calls == before,
                $"{platform}: an ended Core operation with unknown result is neither reported successful nor replayed");
            Check(await coordinator.TrySleepAsync(ServerModules.FirewallId, default) is null && !transport.Disposed,
                $"{platform}: only actual facade task completion permits release; OS/Helper responsibility remains external");
            await CollectAsync(protectedCode);
            Check(!protectedCode.IsAlive, $"{platform}: a retained completed mutation task does not pin execution code");

            transport.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateDenied = service.ListRulesAsync(default);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var lateCode = factory.LastCodeReference!;
            transport.Pending!.SetResult(new(false, ProblemCode: PrivilegedProblemCode.AccessDenied));
            try { await lateDenied; throw new Exception("A late denied read unexpectedly succeeded"); }
            catch (FirewallRulesUnavailableException error) { retained.Add(error); }
            retained.Add(lateDenied);
            Check(await coordinator.TrySleepAsync(ServerModules.FirewallId, default) is null,
                $"{platform}: a genuinely asynchronous failed Helper read finishes before execution release");
            await CollectAsync(lateCode);
            Check(!lateCode.IsAlive,
                $"{platform}: retained late failed task and exception do not capture private completion-continuation frames");

            var port = new FirewallHostOperations(transport); before = transport.Calls;
            try { await port.ExecuteAsync(new((FirewallHostOperation)int.MaxValue), default); throw new Exception("Unrestricted privileged verb accepted"); }
            catch (InvalidOperationException) { }
            Check(transport.Calls == before && !(await port.ExecuteAsync(new(FirewallHostOperation.WindowsStatus, FirewallRuleNumber: 1), default)).Success
                && transport.Calls == before, $"{platform}: unknown verbs and fields belonging to another operation never reach Core transport");
            await VerifyPendingBudgetAsync(fixture, Check);
        }
        WeakReference? failedCode = null;
        var failedActivation = Task.Run(() => new CollectibleFirewallExecution(null!, code => failedCode = code));
        try { await failedActivation; throw new Exception("Invalid activation unexpectedly succeeded"); }
        catch (InvalidDataException error)
        {
            retained.Add(error); retained.Add(failedActivation);
            Check(error.InnerException is null && error.Message == "Bundled Firewall execution could not be activated.",
                "Constructor failure is reconstructed in Host rather than exporting TargetInvocationException or private metadata");
        }
        await CollectAsync(failedCode!);
        Check(failedCode is { IsAlive: false }, "Retaining a failed activation task and exception does not pin its execution context");
        GC.KeepAlive(retained);
        Console.WriteLine($"Firewall boundary checks passed: {count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> RunRetainedVectorsAsync(Fixture fixture, List<object> retained)
    {
        var code = new List<WeakReference>();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            fixture.Transport.Mode = (cycle % 5) switch { 0 or 1 => Mode.Success, 2 => Mode.Denied, 3 => Mode.Malformed, _ => Mode.Cancelled };
            Task task;
            if (fixture.Transport.Mode == Mode.Success && cycle % 5 == 1)
            {
                var read = fixture.Service.ListRulesAsync(default); task = read;
                var rules = await read;
                if (rules.Count != 1 || rules[0].Port != "443") throw new Exception("Real platform rule projection changed");
                retained.Add(rules);
            }
            else if (fixture.Transport.Mode == Mode.Denied)
            {
                var read = fixture.Service.ListRulesAsync(default); task = read;
                try { await read; throw new Exception("Denied rules reported as an authoritative list"); }
                catch (FirewallRulesUnavailableException error)
                { if (error.ProblemCode != "firewall.privileged_proxy_required") throw; retained.Add(error); }
            }
            else
            {
                var read = fixture.Service.GetStatusAsync(default); task = read;
                try
                {
                    var dto = await read;
                    if (fixture.Transport.Mode != Mode.Success || !dto.IsAvailable) throw new Exception("Unexpected read result");
                    retained.Add(dto);
                }
                catch (FirewallExecutionException error) when (fixture.Transport.Mode == Mode.Malformed)
                { if (error.ProblemCode != "firewall.operation_failed") throw; retained.Add(error); }
                catch (OperationCanceledException error) when (fixture.Transport.Mode == Mode.Cancelled) { retained.Add(error); }
            }
            retained.Add(task);
            var reference = fixture.Factory.LastCodeReference!; code.Add(reference);
            if (await fixture.Coordinator.TrySleepAsync(ServerModules.FirewallId, default) is not null)
                throw new Exception("Completed Firewall generation refused release");
            await CollectAsync(reference);
            if (reference.IsAlive) throw new Exception($"Firewall execution context retained at {fixture.Platform} vector {cycle}");
        }
        return code;
    }

    private static async Task VerifyPendingBudgetAsync(Fixture fixture, Action<bool, string> check)
    {
        var factory = new FirewallModuleRuntimeFactory(); var held = new List<AssemblyLoadContext>();
        for (var index = 0; index < 4; index++)
        {
            var runtime = await factory.CreateAsync(fixture.App.Services, fixture.Context, default);
            held.Add(Context(factory.LastCodeReference!));
            await runtime.DisposeAsync();
        }
        var refused = false;
        try { await factory.CreateAsync(fixture.App.Services, fixture.Context, default); }
        catch (InvalidOperationException error) when (error.Message.Contains("code collection is pending", StringComparison.Ordinal)) { refused = true; }
        check(refused, $"{fixture.Platform}: four deliberately retained code contexts refuse a fifth instead of accumulating generations");
        GC.KeepAlive(held);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static AssemblyLoadContext Context(WeakReference reference) =>
        reference.Target is AssemblyLoadContext { IsCollectible: true } context ? context : throw new Exception("Execution is not collectible");
    private static async Task CollectAsync(WeakReference reference)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(10); Collect(); if (!reference.IsAlive) return;
        }
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly HostPlatformKind Platform;
        internal readonly WebApplication App;
        internal readonly ServerModuleContext Context;
        internal readonly FirewallModuleRuntimeFactory Factory = new();
        internal readonly Transport Transport;
        internal readonly ModuleResidencyCoordinator Coordinator;
        internal readonly IHostFirewallService Service;
        private readonly string _root;
        internal Fixture(HostPlatformKind platform)
        {
            Platform = platform; Transport = new(platform);
            _root = Path.Combine(Path.GetTempPath(), "rk-firewall-code-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root }); builder.Logging.ClearProviders();
            Context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory") with { Platform = platform };
            builder.Services.AddSingleton<IPrivilegedOperationTransport>(Transport);
            var ids = new HashSet<string> { ServerModules.FirewallId };
            builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([Factory],
                ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services, Context, services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>()));
            App = builder.Build(); Coordinator = App.Services.GetRequiredService<ModuleResidencyCoordinator>();
            Service = ModuleServiceProxy<IHostFirewallService>.Create(Coordinator, ServerModules.FirewallId);
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Pending?.TrySetResult(new(false, ProblemCode: PrivilegedProblemCode.InternalError));
            await App.DisposeAsync();
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture directory");
            Directory.Delete(_root, true);
        }
    }
    private enum Mode { Success, Denied, Malformed, Cancelled, Pending }
    private sealed class Transport(HostPlatformKind platform) : IPrivilegedOperationTransport, IDisposable
    {
        internal Mode Mode;
        internal int Calls;
        internal bool Disposed;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PrivilegedOperationResult>? Pending;
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Mode == Mode.Pending) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); Entered.TrySetResult(); return Pending.Task; }
            if (Mode == Mode.Cancelled) return Task.FromCanceled<PrivilegedOperationResult>(new(true));
            if (Mode == Mode.Denied) return Task.FromResult(new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.AccessDenied));
            if (Mode == Mode.Malformed) return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: "not-base64"));
            var rules = request.FirewallNumberedStatus == true || request.Operation == PrivilegedOperationKind.FirewallWindowsRules;
            var output = platform == HostPlatformKind.Linux
                ? Encoding.UTF8.GetBytes(rules ? "Status: active\n[ 1] 443/tcp ALLOW IN Anywhere\n[ 2] 443/tcp (v6) ALLOW IN Anywhere (v6)\n"
                    : "Status: active\nDefault: deny (incoming), allow (outgoing)\n")
                : rules ? JsonSerializer.SerializeToUtf8Bytes(new[] { new FirewallRuleDto(1, "allow", "in", "tcp", "any", "any", "443") })
                    : JsonSerializer.SerializeToUtf8Bytes(new FirewallStatusDto(true, true, "windows-defender", null, "deny", "allow"));
            return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(output)));
        }
        public void Dispose() => Disposed = true;
    }
}
