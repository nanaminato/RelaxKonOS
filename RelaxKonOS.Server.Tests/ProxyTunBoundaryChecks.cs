internal static class ProxyTunBoundaryChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label)
        { if (!ok) throw new Exception(label); Console.WriteLine($"PASS TUN BOUNDARY {++count}: {label}"); }
        using (var f = new Fixture())
        {
            f.BeforeReachability = () => Check(File.Exists(f.Marker) && f.Publication.Gate.CurrentCount == 0,
                "Activation owns the publication gate and persists its safety marker before contacting the engine");
            Check(await f.Safety.EnableAsync(Guid.NewGuid(), IPAddress.Parse("192.0.2.20"), default) is null,
                "The real TUN executor activates through the safety service");
            var activation = MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active));
            Check(activation.Enabled && activation.RouteExclusions.Contains("192.0.2.20/32") && f.Devices.Calls == 1,
                "Activation preserves the captured management path and requires adapter observation");
            var writer = f.Writer();
            Check(await writer.UpdateAsync(Request((await f.Reader.GetAsync(default)) with { MixedPort = 8123 }), default) is null,
                "An independent settings writer can update the live configuration");
            var rewritten = MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active));
            Check(rewritten.Enabled && rewritten.RouteExclusions.SequenceEqual(activation.RouteExclusions),
                "Settings writes preserve the active TUN flag and exact exclusions");
            Check(await f.Safety.DisableAsync(default) is null && !File.Exists(f.Marker)
                && !MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active)).Enabled,
                "Disable clears the marker only after runtime and management-route restoration");
        }
        foreach (var failure in new[] { "reload", "adapter", "route" })
        {
            using var f = new Fixture();
            if (failure == "reload") f.FailReloads = 1;
            if (failure == "adapter") f.Devices.Active = false;
            if (failure == "route") f.Platform.ManagementRouteVerifies = false;
            var result = await f.Safety.EnableAsync(Guid.NewGuid(), null, default);
            Check(result == (failure == "route" ? ProxyProblemCodes.ManagementRouteUnsafe : ProxyProblemCodes.TunActivationFailed)
                && !File.Exists(f.Marker) && f.Platform.RestoreCount == 1
                && !MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active)).Enabled,
                "Failed activation compensates through the real executor and safety owner: " + failure);
        }
        using (var f = new Fixture())
        {
            f.Reachable = false;
            Check(await f.Safety.EnableAsync(Guid.NewGuid(), null, default) == ProxyProblemCodes.RecoveryRequired
                && File.Exists(f.Marker) && f.Reloads == 0,
                "Unavailable controller never reloads or discards unresolved recovery responsibility");
            var marker = await File.ReadAllTextAsync(f.Marker);
            Check(await f.RecoverAsync() == ProxyProblemCodes.RecoveryRequired
                && await File.ReadAllTextAsync(f.Marker) == marker,
                "A reopened safety owner retains the same unresolved operation marker");
            f.Reachable = true;
            Check(await f.RecoverAsync() is null && !File.Exists(f.Marker),
                "Startup recovery retries teardown after the controller becomes available");
        }
        using (var f = new Fixture())
        {
            await f.Safety.EnableAsync(Guid.NewGuid(), null, default);
            f.ObservationFails = true;
            Check(await f.Safety.EnableAsync(Guid.NewGuid(), null, default) == ProxyProblemCodes.RecoveryRequired && File.Exists(f.Marker),
                "An unobserved engine cannot invalidate the active durable marker");
            Check(await f.RecoverAsync() is null && !File.Exists(f.Marker),
                "Restart recovery tears down an active session even when live observation fails");
        }
        foreach (var published in new[] { false, true })
        {
            using var f = new Fixture();
            var before = await File.ReadAllTextAsync(f.Active);
            var candidate = new RelaxKonOS.Protocol.ManagedRuntimes.MihomoPublishedRuntime(before, [], 9090,
                "controller-secret", [new("127.0.0.1", 7890)]);
            var operation = Guid.NewGuid();
            await f.Publication.BeginAsync(operation, 0, candidate, default);
            if (published) await f.Publication.ConfirmAsync(operation, MihomoPublicationStore.Identity(before), default);
            Check(await f.Tun.SetEnabledAsync(f.Snapshot, true, default) is not null
                && await f.Tun.SetEnabledAsync(f.Snapshot, false, default) is not null
                && f.Reloads == 0 && await File.ReadAllTextAsync(f.Active) == before,
                "Direct TUN transitions cannot bypass pending or confirmed managed publication: " + published);
        }
        using (var f = new Fixture())
        {
            f.Platform.ManagementRouteVerifies = false;
            f.Platform.RestoreSucceeds = false;
            Check(await f.Safety.EnableAsync(Guid.NewGuid(), null, default) == ProxyProblemCodes.RecoveryRequired
                && File.Exists(f.Marker), "Failed route compensation retains the durable recovery marker");
            f.Platform.RestoreSucceeds = true;
            Check(await f.RecoverAsync() is null && !File.Exists(f.Marker),
                "A reopened safety owner completes the previously failed platform compensation");
        }
        using (var f = new Fixture())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.BlockReload = async ct => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
            using var cancelled = new CancellationTokenSource();
            var enable = f.Safety.EnableAsync(Guid.NewGuid(), null, cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            try { await enable; throw new Exception("Cancelled activation succeeded"); }
            catch (OperationCanceledException) { }
            Check(File.Exists(f.Marker), "Cancellation after YAML replacement retains the unresolved activation marker");
            Check(await f.RecoverAsync() is null && !File.Exists(f.Marker),
                "Recovery after cancelled activation reacquires both gates and tears down the runtime");
        }
        using (var f = new Fixture())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.BlockReload = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); };
            var write = f.Writer().UpdateAsync(Request((await f.Reader.GetAsync(default)) with { MixedPort = 8123 }), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            var tun = f.Tun.SetEnabledAsync(f.Snapshot, false, cancelled.Token);
            Check(!tun.IsCompleted, "TUN waits while an independent settings writer owns the publication gate");
            cancelled.Cancel();
            try { await tun; throw new Exception("Cancelled TUN acquired the writer gate"); }
            catch (OperationCanceledException) { }
            finally { release.TrySetResult(); }
            Check(await write is null && await f.Tun.SetEnabledAsync(f.Snapshot, false, default) is null,
                "Cancelled TUN does not release the writer gate and later teardown remains usable");
        }
        using (var f = new Fixture())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.BlockReload = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); };
            var enable = f.Safety.EnableAsync(Guid.NewGuid(), null, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var write = f.Writer().UpdateAsync(Request((await f.Reader.GetAsync(default)) with { MixedPort = 8123 }), default);
            var disable = f.Safety.EmergencyDisableAsync(default);
            try { Check(!write.IsCompleted && !disable.IsCompleted,
                "A live activation protects both publication writes and safety teardown from concurrent execution"); }
            finally { release.TrySetResult(); }
            Check(await enable is null && await write is null && await disable is null && !File.Exists(f.Marker)
                && !MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active)).Enabled,
                "Competing settings and teardown drain without deadlock or stale TUN reactivation");
        }
        using (var f = new Fixture())
        {
            await f.Safety.EnableAsync(Guid.NewGuid(), null, default);
            var old = (await f.Safety.CaptureRecoveryAsync(default))!;
            await f.Safety.DisableAsync(default);
            await f.Safety.EnableAsync(Guid.NewGuid(), null, default);
            var reloads = f.Reloads;
            Check(await f.Safety.RecoverAsync(old, default) is null && f.Reloads == reloads
                && (await f.Safety.GetStatusAsync(default)).HasRecoveryMarker
                && MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(f.Active)).Enabled,
                "A delayed recovery for an old operation cannot tear down a newly established TUN session");
            Check((await f.Safety.CaptureRecoveryAsync(default))!.OperationId != old.OperationId,
                "A new TUN activation has its own durable recovery identity");
        }
        foreach (var corrupt in new[] { "", "{", "null", "{}" })
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Paths.GetStateDirectory());
            await File.WriteAllTextAsync(f.Marker, corrupt);
            var reference = (await f.Safety.CaptureRecoveryAsync(default))!;
            Check(await f.Safety.RecoverAsync(reference, default) == ProxyProblemCodes.RecoveryRequired && f.Reloads == 0
                && await File.ReadAllTextAsync(f.Marker) == corrupt,
                "Unknown TUN markers remain blocked without native changes or synthetic success");
        }
        Console.WriteLine($"TUN execution boundary checks passed: {count}");
    }

    private static UpdateProxySettingsRequest Request(ProxySettingsDto s) => new(s.SystemProxyEnabled, s.AllowLan,
        s.DnsEnabled, s.Ipv6Enabled, s.UnifiedDelay, s.LogLevel, s.MixedPort, s.AllowInsecureSubscriptionSources,
        s.SystemProxyHost, s.Tun, s.SystemProxy);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "proxy-tun-boundary-" + Guid.NewGuid().ToString("N"));
        public TestProxyPaths Paths { get; }
        public IProxySettingsReader Reader { get; }
        public MihomoPublicationStore Publication { get; }
        public Devices Devices { get; } = new();
        public TestProxyNetworkSafetyPlatform Platform { get; } = new() { SnapshotSafe = true };
        public IMihomoControllerClient Controller { get; }
        public MihomoTunRuntimeController Tun { get; }
        public ProxyTunSafetyService Safety { get; }
        public string Active => Path.Combine(Paths.GetProtectedConfigurationDirectory(), "active.yaml");
        public string Marker => Path.Combine(Paths.GetStateDirectory(), "proxy-tun-recovery.json");
        public ProxyManagementRouteSnapshot Snapshot { get; } = new("boundary", DateTimeOffset.UtcNow, true, "eth0", "192.0.2.1", [], []);
        public bool Reachable = true, ObservationFails;
        public int Reloads, FailReloads;
        public Action? BeforeReachability;
        public Func<CancellationToken, Task>? BlockReload;
        public Fixture()
        {
            Paths = new(root); Reader = new ProxySettingsReader(Paths);
            Directory.CreateDirectory(Paths.GetProtectedConfigurationDirectory());
            File.WriteAllText(Active, "mixed-port: 7890\nmode: rule\nproxies: []\nproxy-groups: []\nrules: []\n");
            Publication = new(Paths, new EphemeralDataProtectionProvider());
            Controller = DispatchProxy.Create<IMihomoControllerClient, ControllerStub>();
            ((ControllerStub)Controller).InvokeMethod = (name, args) => name switch
            {
                "IsReachableAsync" => Reachability(),
                "ReloadAsync" => ReloadAsync((CancellationToken)args![0]!),
                "GetTunEnabledAsync" => Task.FromResult(ObservationFails
                    ? ControllerResult<bool>.Failure(ProxyProblemCodes.ControllerUnavailable)
                    : ControllerResult<bool>.Success(MihomoManagedConfiguration.ReadTunActivation(File.ReadAllText(Active)).Enabled)),
                _ => throw new Exception("Unexpected controller method: " + name)
            };
            Tun = new(Paths, Reader, Controller, Publication, Devices);
            Safety = NewSafety();
        }
        private Task<ControllerResult<bool>> Reachability()
        {
            var check = BeforeReachability; BeforeReachability = null; check?.Invoke();
            return Task.FromResult(Reachable ? ControllerResult<bool>.Success(true)
                : ControllerResult<bool>.Failure(ProxyProblemCodes.ControllerUnavailable));
        }
        private async Task<string?> ReloadAsync(CancellationToken ct)
        {
            Reloads++;
            var block = BlockReload; BlockReload = null;
            if (block is not null) await block(ct);
            if (FailReloads > 0) { FailReloads--; return ProxyProblemCodes.TunActivationFailed; }
            return null;
        }
        public async Task<string?> RecoverAsync()
        {
            var safety = NewSafety();
            var reference = await safety.CaptureRecoveryAsync(default);
            return reference is null ? null : await safety.RecoverAsync(reference, default);
        }
        public ProxyTunSafetyService NewSafety() => new(Paths, Platform, Tun);
        public MihomoSettingsService Writer() => new(Paths, Controller, new StaticProxySecretStore(),
            new MihomoControllerOptions(), new Host(), Publication);
        public void Dispose() => Directory.Delete(root, true);
    }
    public class ControllerStub : DispatchProxy
    {
        public Func<string, object?[]?, object?> InvokeMethod = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!.Name, args);
    }
    private sealed class Devices : IProxyTunDeviceObserver
    {
        public bool Active = true;
        public int Calls;
        public Task<bool> WaitForActiveAsync(string name, CancellationToken ct) { Calls++; return Task.FromResult(Active); }
    }
    private sealed class Host : IHostSystemProxyService
    {
        public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new ProxySystemProxyCapabilities(true, true, true, true));
        public Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
