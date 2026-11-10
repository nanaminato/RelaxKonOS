internal static class SystemProxyChecks
{
    public static async Task RunAsync()
    {
        var capabilities = new ProxySystemProxyCapabilities(true, false, true, true);
        var wire = JsonSerializer.Serialize(capabilities);
        TestAssert.Assert(wire.Contains("\"supported\":true") && wire.Contains("\"supportsPac\":false"), "Helper capability names must match the public wire contract.");
        try { JsonSerializer.Deserialize<ProxySystemProxyCapabilities>("{\"supported\":true}"); throw new Exception("Missing capability fields were accepted."); }
        catch (JsonException) { }
        var prior = ProxySettingsReader.Defaults with { MixedPort = 7890, LogLevel = "info" };
        var listenerCandidate = prior with { MixedPort = 8123, LogLevel = "warning" };
        var intent = new MihomoSettingsPublication(null, prior, listenerCandidate);
        const string previousYaml = "mode: rule\nmixed-port: 7890\nlog-level: info\n";
        const string candidateYaml = "mode: rule\nmixed-port: 8123\nlog-level: warning\n";
        TestAssert.Assert(MihomoDirectSettingsCompensationService.HasObservableListenerDelta(previousYaml,
            MihomoPublicationStore.Identity(candidateYaml), intent), "The exact observable listener delta was rejected.");
        TestAssert.Assert(!MihomoDirectSettingsCompensationService.HasObservableListenerDelta(previousYaml,
            MihomoPublicationStore.Identity(candidateYaml + "ipv6: true\n"), intent), "An unobserved YAML change was accepted.");
        TestAssert.Assert(!MihomoDirectSettingsCompensationService.HasObservableListenerDelta(previousYaml,
            MihomoPublicationStore.Identity(candidateYaml),
            intent with { Candidate = listenerCandidate with { AllowLan = !listenerCandidate.AllowLan } }),
            "An unobserved settings change was accepted.");
        using (var fixture = new Fixture())
        {
            var original = await File.ReadAllTextAsync(fixture.Active);
            var current = await fixture.Reader.GetAsync(default);
            fixture.Host.Capabilities = new(false, false, false, false);
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true }), default) == ProxyProblemCodes.NotSupported,
                "Unavailable writer was enabled.");
            fixture.Host.Capabilities = capabilities;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true, SystemProxy = ProxySystemProxyOptionsDto.Default with { UsePac = true } }), default)
                == ProxyProblemCodes.NotSupported, "Linux PAC was accepted.");
            TestAssert.Assert(fixture.Host.Calls.Count == 0 && await File.ReadAllTextAsync(fixture.Active) == original, "Capability rejection mutated configuration.");
            fixture.Host.Problem = ProxyProblemCodes.SystemProxyConflict;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true, MixedPort = 8123 }), default) == ProxyProblemCodes.SystemProxyConflict,
                "Host conflict was discarded.");
            TestAssert.Assert(await File.ReadAllTextAsync(fixture.Active) == original && !(await fixture.Reader.GetAsync(default)).SystemProxyEnabled,
                "Failed host application did not roll back YAML and saved settings.");
            fixture.Host.Problem = ProxyProblemCodes.PrivilegedOperationUnavailable;
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { MixedPort = 8123 }), default) is null,
                "Absent helper blocked an unrelated setting while the proxy was disabled.");
            TestAssert.Assert(await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) == ProxyProblemCodes.PrivilegedOperationUnavailable,
                "Startup hid unavailable recovery.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Reader.GetAsync(default);
            var file = Path.Combine(fixture.Paths.GetStateDirectory(), "mihomo-settings.json");
            var external = JsonSerializer.Serialize(current with { LogLevel = "error" });
            fixture.Host.BeforeApply = () => File.WriteAllText(file, external);
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { LogLevel = "info" }), default) == ProxyProblemCodes.RecoveryRequired,
                "An independent settings edit should leave an acknowledged direct update pending.");
            var pending = (await fixture.Publication.ReadAsync(default)).DirectReload!;
            var recovery = new RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService(fixture.Publication,
                null!, null!, null!, null!, settingsFiles: new(fixture.Paths, fixture.Host));
            try
            {
                await recovery.RetryAsync(pending.OperationId, default);
                throw new Exception("Settings recovery accepted an independent edit.");
            }
            catch (RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime.settings_drift") { }
            TestAssert.Assert(pending.ConfirmedSettings is not null && await File.ReadAllTextAsync(file) == external
                && await fixture.Publication.ReadPendingIdentityAsync(default) == pending.OperationId && fixture.Host.Calls.Count == 1,
                "Conflicting settings recovery mutated the file, repeated the host operation, or dropped the original checkpoint.");
            File.Delete(file); // Restore the prior absence in this private fixture.
            var completed = await recovery.RetryAsync(pending.OperationId, default);
            TestAssert.Assert(completed.Outcome == RelaxKonOS.Protocol.ManagedRuntimes.RuntimeSwitchOutcome.Succeeded
                && await recovery.RetryAsync(pending.OperationId, default) == completed
                && (await fixture.Reader.GetAsync(default)).LogLevel == "info" && fixture.Host.Calls.Count == 1
                && await fixture.Publication.ReadPendingIdentityAsync(default) is null,
                "The acknowledged local settings commit should complete once without another host effect.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Reader.GetAsync(default);
            var original = await File.ReadAllTextAsync(fixture.Active);
            fixture.Host.BeforeApply = () => Directory.CreateDirectory(Path.Combine(fixture.Paths.GetStateDirectory(), "mihomo-settings.json")); // Fail persistence after the valid read and host apply.
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true }), default) == ProxyProblemCodes.ConfigApplyFailed,
                "Failed persistence was reported as a successful proxy enable.");
            TestAssert.Assert(fixture.Host.Calls.Count == 2 && !fixture.Host.Calls[1].Settings.SystemProxyEnabled && fixture.Host.Calls[1].Enforce,
                "Persistence failure did not compensate host settings.");
            TestAssert.Assert(await File.ReadAllTextAsync(fixture.Active) == original, "Persistence failure left updated YAML.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Reader.GetAsync(default);
            var enabled = current with { SystemProxyEnabled = true, MixedPort = 8123, SystemProxy = ProxySystemProxyOptionsDto.Default with { GuardEnabled = true } };
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(enabled), default) is null, "Supported Linux writer was rejected.");
            TestAssert.Assert((await fixture.Reader.GetAsync(default)).SystemProxyEnabled, "Applied settings were not saved.");
            await fixture.Reconciler.ReconcileSystemProxyAsync(false, default);
            TestAssert.Assert(fixture.Host.Calls[^1].Enforce, "Configured guard did not enforce the host proxy.");
            var before = fixture.Host.Calls.Count;
            fixture.Host.BlockNext = true;
            using var caller = new CancellationTokenSource();
            var disable = fixture.Service.UpdateAsync(Request(enabled with { SystemProxyEnabled = false }), caller.Token);
            await fixture.Host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var guard = fixture.Reconciler.ReconcileSystemProxyAsync(false, default);
            TestAssert.Assert(!disable.IsCompleted && !guard.IsCompleted, "The accepted write and its guard gate must survive caller cancellation.");
            fixture.Host.Release.TrySetResult();
            TestAssert.Assert(await disable is null && await guard is null, "Concurrent disable/guard failed.");
            TestAssert.Assert(fixture.Host.Calls.Count == before + 1 && !fixture.Host.Calls[^1].Settings.SystemProxyEnabled,
                "Stale guard reapplied the proxy after it was disabled.");
            await fixture.Reconciler.ReconcileSystemProxyAsync(true, default);
            TestAssert.Assert(!fixture.Host.Calls[^1].Settings.SystemProxyEnabled, "Startup did not attempt root-side cleanup while disabled.");
        }
        using (var fixture = new Fixture())
        {
            var current = await fixture.Reader.GetAsync(default);
            var original = await File.ReadAllTextAsync(fixture.Active);
            await fixture.Publication.BeginAsync(Guid.NewGuid(), 0,
                new RelaxKonOS.Protocol.ManagedRuntimes.MihomoPublishedRuntime("# pending publication\n", [], 9090,
                    "pending-controller-secret", [new("127.0.0.1", 7890)]), default);
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { MixedPort = 8123 }), default) == ProxyProblemCodes.RecoveryRequired
                && await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) == ProxyProblemCodes.RecoveryRequired
                && await File.ReadAllTextAsync(fixture.Active) == original && fixture.Host.Calls.Count == 0,
                "Settings or the system-proxy guard bypassed a pending managed publication.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Host.Supported = true;
            var saved = (await fixture.Reader.GetAsync(default)) with { SystemProxyEnabled = true };
            await File.WriteAllBytesAsync(Path.Combine(fixture.Paths.GetStateDirectory(), "mihomo-settings.json"),
                JsonSerializer.SerializeToUtf8Bytes(saved));
            var changed = saved with { MixedPort = 8123 };
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(changed), default) == ProxyProblemCodes.RecoveryRequired,
                "The port switch must retain its original unconfirmed host receipt.");
            var pending = (await fixture.Publication.ReadAsync(default)).DirectReload!;
            await fixture.Publication.DeferTrackedHostGuardUnderGateAsync(await fixture.Publication.ReadAsync(default));
            TestAssert.Assert((await fixture.Publication.ReadAsync(default)).DirectReload
                is { HostGuardDeferred: true, HostGuardSuperseded: false }, "Deferral must preserve the original authority.");
            fixture.Host.EffectMatches = true;
            TestAssert.Assert(await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) is null
                && (await fixture.Publication.ReadAsync(default)).HasDirectCompletion(pending.OperationId)
                && (await fixture.Reader.GetAsync(default)).MixedPort == changed.MixedPort && fixture.Host.Calls.Count == 0,
                "Guard deferral must leave the original receipt eligible for read-only completion.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Host.Supported = true;
            var saved = (await fixture.Reader.GetAsync(default)) with
            {
                SystemProxyEnabled = true,
                SystemProxy = ProxySystemProxyOptionsDto.Default with { GuardEnabled = true }
            };
            await File.WriteAllBytesAsync(Path.Combine(fixture.Paths.GetStateDirectory(), "mihomo-settings.json"),
                JsonSerializer.SerializeToUtf8Bytes(saved));
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(saved with { MixedPort = 8123 }), default)
                == ProxyProblemCodes.RecoveryRequired, "The candidate receipt should still be pending.");
            TestAssert.Assert(await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) is null
                && fixture.Host.Calls is [{ Enforce: true } call] && call.Settings == saved
                && (await fixture.Publication.ReadAsync(default)).DirectReload is { HostGuardSuperseded: true, HostGuardDeferred: false },
                "Without authenticated listener evidence, the guard must enforce the saved policy.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Host.Supported = true;
            var current = await fixture.Reader.GetAsync(default);
            var candidate = current with { SystemProxyEnabled = true };
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(candidate with
                { SystemProxy = ProxySystemProxyOptionsDto.Default with { BypassList = "<local>" } }), default) == ProxyProblemCodes.ConfigInvalid
                && (await fixture.Publication.ReadAsync(default)).DirectReload is null && fixture.Host.TrackedId is null,
                "Invalid Linux proxy policy must fail before the original checkpoint or native mutation.");
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(candidate), default) == ProxyProblemCodes.RecoveryRequired,
                "The original host acknowledgement was intentionally lost.");
            var pending = (await fixture.Publication.ReadAsync(default)).DirectReload!;
            TestAssert.Assert(pending.TrackedHostPolicy && fixture.Host.TrackedId == pending.OperationId && fixture.Host.Calls.Count == 0,
                "Only the original tracked mutation may run before recovery.");
            fixture.Host.EffectMatches = true;
            TestAssert.Assert(await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) is null
                && (await fixture.Reader.GetAsync(default)).SystemProxyEnabled
                && (await fixture.Publication.ReadAsync(default)).HasDirectCompletion(pending.OperationId)
                && fixture.Host.Calls.Count == 0 && fixture.Host.Queries == 1,
                "Guard should settle a proved original operation without a host write.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Host.Supported = true;
            var current = await fixture.Reader.GetAsync(default);
            TestAssert.Assert(await fixture.Service.UpdateAsync(Request(current with { SystemProxyEnabled = true }), default) == ProxyProblemCodes.RecoveryRequired
                && await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) is null,
                "The original guard authority must continue when the receipt is unavailable.");
            var pending = (await fixture.Publication.ReadAsync(default)).DirectReload!;
            fixture.Host.EffectMatches = true;
            var recovery = new RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService(fixture.Publication,
                null!, null!, null!, null!, settingsFiles: new(fixture.Paths, fixture.Host));
            TestAssert.Assert(pending.HostGuardSuperseded && fixture.Host.Calls is [{ Settings.SystemProxyEnabled: false }]
                && (await recovery.RetryAsync(pending.OperationId, default)).Outcome == RelaxKonOS.Protocol.ManagedRuntimes.RuntimeSwitchOutcome.RecoveryRequired
                && fixture.Host.Queries == 1 && !(await fixture.Reader.GetAsync(default)).SystemProxyEnabled,
                "A later apparently matching receipt cannot claim an intervening guard action.");
            var marker = Path.Combine(fixture.Paths.GetStateDirectory(), "proxy-tun-recovery.json");
            await File.WriteAllTextAsync(marker, "{");
            var compensation = new MihomoDirectSettingsCompensationService(fixture.Paths, fixture.Publication,
                new(fixture.Paths, fixture.Host), new HealthyMihomoController(), new StaticProxySecretStore());
            try
            {
                await compensation.CompensateAsync(pending.OperationId, true, default,
                    _ => throw new Exception("Compensation accepted a lease from a missing owner."));
                throw new Exception("Compensation bypassed the original management owner.");
            }
            catch (RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime.publication_work_unavailable") { }
            try
            {
                await compensation.CompensateAsync(pending.OperationId, true, default);
                throw new Exception("Compensation accepted an unresolved TUN marker.");
            }
            catch (RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime.direct_compensation_unavailable") { }
            TestAssert.Assert((await fixture.Publication.ReadAsync(default)).DirectReload == pending
                && fixture.Host.Calls.Count == 1 && await File.ReadAllTextAsync(marker) == "{",
                "The compensation gate must preserve the TUN marker and original authority.");
        }
        using (var fixture = new Fixture())
        {
            await fixture.Publication.Gate.WaitAsync();
            using var cancelled = new CancellationTokenSource();
            var guard = fixture.Reconciler.ReconcileSystemProxyAsync(true, cancelled.Token);
            cancelled.Cancel();
            try { await guard; throw new Exception("Cancelled guard acquired a busy publication gate."); }
            catch (OperationCanceledException) { }
            finally { fixture.Publication.Gate.Release(); }
            TestAssert.Assert(fixture.Host.Calls.Count == 0 && await fixture.Reconciler.ReconcileSystemProxyAsync(true, default) is null,
                "Cancelled guard must not write or release another owner's gate; a later pass remains usable.");
        }
        Console.WriteLine("System proxy capability, PAC gating, apply/persist rollback, concurrent guard and pending publication checks passed.");
    }
    private static UpdateProxySettingsRequest Request(ProxySettingsDto settings) => new(settings.SystemProxyEnabled, settings.AllowLan,
        settings.DnsEnabled, settings.Ipv6Enabled, settings.UnifiedDelay, settings.LogLevel, settings.MixedPort,
        settings.AllowInsecureSubscriptionSources, settings.SystemProxyHost, settings.Tun, settings.SystemProxy);
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "relaxkonos-system-proxy-test-" + Guid.NewGuid().ToString("N"));
        public TestProxyPaths Paths { get; }
        public HostWriter Host { get; } = new();
        public MihomoSettingsService Service { get; }
        public IProxySystemProxyReconciler Reconciler => new MihomoSystemProxyReconciler(Paths, Reader, new StaticProxySecretStore(), Host, Publication,
            settingsFiles: new(Paths, Host));
        public IProxySettingsReader Reader => new ProxySettingsReader(Paths);
        public MihomoPublicationStore Publication { get; }
        public string Active => Path.Combine(Paths.GetProtectedConfigurationDirectory(), "active.yaml");
        public Fixture()
        {
            Paths = new(root);
            Directory.CreateDirectory(Paths.GetProtectedConfigurationDirectory());
            Directory.CreateDirectory(Paths.GetStateDirectory());
            File.WriteAllText(Active, "mixed-port: 7890\nmode: rule\nproxies: []\nproxy-groups: []\nrules: []\n");
            Publication = new(Paths, new EphemeralDataProtectionProvider());
            Service = new(Paths, new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), Host, Publication, new(Paths, Host));
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
    private sealed class HostWriter : IHostSystemProxyService, ITrackedHostSystemProxy
    {
        public bool Supported { get; set; }
        public bool EffectMatches;
        public Guid? TrackedId;
        public int Queries;
        public Task<bool> ApplyTrackedAsync(Guid id, ProxySettingsDto settings, CancellationToken ct)
        {
            if (TrackedId is not null) throw new Exception("Tracked host operation was replayed.");
            TrackedId = id;
            return Task.FromResult(false);
        }
        public Task<LinuxSystemProxyReceipt?> ObserveTrackedEffectAsync(Guid id, ProxySettingsDto settings, CancellationToken ct)
        {
            Queries++;
            return Task.FromResult<LinuxSystemProxyReceipt?>(id == TrackedId ? new(id, Applied: true, Current: EffectMatches) : null);
        }
        public ProxySystemProxyCapabilities Capabilities = new(true, false, true, true);
        public readonly List<(ProxySettingsDto Settings, bool Enforce)> Calls = [];
        public string? Problem;
        public bool BlockNext;
        public Action? BeforeApply;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Capabilities);
        public async Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken cancellationToken)
        {
            Calls.Add((settings, enforce));
            var before = BeforeApply; BeforeApply = null; before?.Invoke();
            if (BlockNext) { BlockNext = false; Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return Problem;
        }
    }
}
