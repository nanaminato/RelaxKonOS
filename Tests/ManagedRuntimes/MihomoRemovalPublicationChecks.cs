using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using System.Text.Json.Nodes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoRemovalPublicationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected removal publication refusal: " + code);
        }
        foreach (var keep in new[] { true, false })
        {
            using var recoveryFixture = new Fixture(keep) { Mode = "lost-response" };
            await recoveryFixture.SeedAsync();
            try { await recoveryFixture.RemoveAsync(); throw new Exception("Expected transport loss"); }
            catch (IOException) { }
            try { await recoveryFixture.Recovery.RetryAsync(Guid.NewGuid(), default); throw new Exception("Expected named recovery refusal"); }
            catch (RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime.recovery_target_conflict") { count++; }
            Check((await recoveryFixture.Recovery.RetryAsync(recoveryFixture.Operation, default)).Outcome == RuntimeSwitchOutcome.Succeeded
                && recoveryFixture.Removals == 1 && (await recoveryFixture.Store.ReadAsync(default)).Pending is null,
                "Public recovery coordinator finishes the named removal commit without another uninstall: " + keep);
        }
        foreach (var keep in new[] { true, false })
        {
            using var f = new Fixture(keep); await f.SeedAsync();
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Confirmed && f.Removals == 1 && f.GateHeld,
                "Removal confirms the exact receipt under the shared gate: " + keep);
            var state = await f.Store.ReadAsync(default);
            Check(state.Pending is null && (state.Published is not null) == keep && File.Exists(f.Active) == keep
                && state.LastRemoval is { Confirmed: true }, "Retention/deletion is reflected by both authority and mirror: " + keep);
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Confirmed && f.Removals == 1,
                "Successful removal replay does not repeat Helper deletion: " + keep);
            await Denied("idempotency_conflict", () => f.Service.RemoveAsync(f.Operation,
                f.Plan with { Request = f.Plan.Request with { KeepData = !keep } }, default));
        }
        using (var f = new Fixture(false) { Mode = "failed" })
        {
            await f.SeedAsync();
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Compensated
                && (await f.Store.ReadAsync(default)).Published is not null && File.Exists(f.Active),
                "Measured removal compensation retains the original publication");
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Compensated && f.Removals == 1,
                "Failed removal replays its receipt without another uninstall");
        }
        foreach (var keep in new[] { true, false })
        {
            using var f = new Fixture(keep) { Mode = "lost-response" }; await f.SeedAsync();
            try { await f.RemoveAsync(); throw new Exception("Expected lost removal response"); }
            catch (IOException) { count++; }
            Check((await f.Store.ReadAsync(default)).Pending?.RemovalPlan is not null && File.Exists(f.Active),
                "Lost Helper response leaves durable removal pending before mirror cleanup");
            f.Recreate();
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Configuration, f.Installation, f.Service,
                NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default); await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Check((await f.Store.ReadAsync(default)) is { Pending: null, LastRemoval.Confirmed: true }
                && File.Exists(f.Active) == keep && f.Removals == 1 && f.Recoveries == 0,
                "Startup reconciles a completed removal without reinstalling or rerunning deletion: " + keep);
        }
        using (var f = new Fixture(false))
        {
            await f.SeedAsync();
            var state = await f.Store.ReadAsync(default);
            await f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan, await f.Files.CaptureRemovalAsync(default), default);
            await Denied("publication_unconfirmed", () => f.Authorization.ReadAsync(default));
            await Denied("publication_unconfirmed", () => f.Authorization.ReadRemovalAsync(Guid.NewGuid(), default));
            Check((await f.Authorization.ReadRemovalAsync(f.Operation, default)).Published!.Yaml == state.Published!.Yaml,
                "Only the exact pending removal can obtain compensation authority");
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.NotStarted
                && f.Recoveries == 1 && f.Removals == 0 && File.Exists(f.Active),
                "Server-only acceptance closes only after Helper lock-backed absence with original installation intact");
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.NotStarted && f.Removals == 0,
                "NotStarted replay never initiates a new removal");
        }
        using (var f = new Fixture(false) { Mode = "recovery" })
        {
            await f.SeedAsync();
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.RecoveryRequired
                && (await f.Store.ReadAsync(default)).Pending is not null && File.Exists(f.Active),
                "Unresolved compensation preserves original authority and mirror");
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Compensated
                && f.Removals == 1 && f.Recoveries == 1, "Removal recovery only restores the original transaction");
        }
        foreach (var mode in new[] { "wrong-identity", "wrong-instance", "mirror-drift", "mirror-directory", "wrong-revision" })
        {
            using var f = new Fixture(false) { Mode = mode }; await f.SeedAsync();
            await Denied(mode == "wrong-instance" ? "installation_mismatch" : "publication_unconfirmed", () => f.RemoveAsync());
            Check((await f.Store.ReadAsync(default)).Pending is not null && (File.Exists(f.Active) || Directory.Exists(f.Active)),
                "Conflicting proof cannot erase Server authority or an unrelated file: " + mode);
        }
        using (var f = new Fixture(false))
        {
            await f.SeedAsync();
            var state = await f.Store.ReadAsync(default);
            await f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan, await f.Files.CaptureRemovalAsync(default), default);
            f.Commit(true); File.Delete(f.Active); f.Recreate();
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && (await f.Store.ReadAsync(default)).Published is null,
                "Recovery accepts the exact crash window after mirror deletion before encrypted authority commit");
        }
        foreach (var keep in new[] { true, false })
        {
            using var f = new Fixture(keep); await f.SeedAsync();
            var preferences = await f.SeedHostAsync();
            f.Host.HttpsProxy = "http://externally-edited.invalid:9443";
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Confirmed && f.Host.HttpProxy == MihomoRemovalHostFixture.Original
                && f.Host.HttpsProxy == "http://externally-edited.invalid:9443" && f.Host.Calls == 1,
                "Removal restores owned proxy keys while preserving external edits: " + keep);
            Check(await f.ReadSettingsAsync() == (preferences with { SystemProxyEnabled = false }),
                "Removal persists only disabled proxy activation and preserves every other app preference: " + keep);
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Confirmed && f.Removals == 1 && f.Host.Calls == 1,
                "Completed removal replay never repeats host cleanup: " + keep);
        }
        using (var f = new Fixture(false) { Mode = "failed" })
        {
            await f.SeedAsync(); var previous = await f.SeedHostAsync();
            Check((await f.RemoveAsync()).Outcome == MihomoPublicationOutcome.Compensated && f.Host.Calls == 0
                && f.Host.HttpProxy!.Contains("18888", StringComparison.Ordinal) && await f.ReadSettingsAsync() == previous,
                "Compensated native removal retains the original host proxy and preferences");
        }
        foreach (var keep in new[] { true, false })
        foreach (var fault in new[] { "reject", "unavailable", "lost-host", "settings-drift", "settings-directory", "snapshot-drift" })
        {
            using var f = new Fixture(keep); await f.SeedAsync(); await f.SeedHostAsync();
            if (fault == "reject") f.Host.Failures = 1;
            if (fault == "unavailable") f.Host.UnavailableFailures = 1;
            if (fault == "lost-host") f.Host.LoseDisableResponse = true;
            if (fault == "settings-drift") f.Host.AfterDisable = () => File.WriteAllText(f.SettingsPath, "unrelated settings");
            if (fault == "settings-directory") f.Host.AfterDisable = () => { File.Delete(f.SettingsPath); Directory.CreateDirectory(f.SettingsPath); };
            if (fault == "snapshot-drift") f.Host.AfterDisable = () => f.AdvanceRevision();
            try { await f.RemoveAsync(); throw new Exception("Host removal fault was not injected"); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode is "runtime.settings_host_apply_failed" or "runtime.helper_unavailable"
                or "runtime.settings_drift" or "runtime.publication_unconfirmed") { count++; }
            catch (IOException) when (fault == "lost-host") { count++; }
            Check((await f.Store.ReadAsync(default)) is { Pending.Settings: not null, LastRemoval: null }
                && f.Removals == 1 && File.Exists(f.Active),
                "Unfinished host cleanup retains exact intent and Server mirror after proved native removal: " + keep + "/" + fault);
            if (fault is "reject" or "unavailable" or "lost-host")
            {
                f.Recreate();
                Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed
                    && f.Removals == 1 && f.Recoveries == 0 && f.Host.HttpProxy == MihomoRemovalHostFixture.Original
                    && !(await f.ReadSettingsAsync()).SystemProxyEnabled && (await f.Store.ReadAsync(default)).Pending is null,
                    "Named recovery finishes host cleanup without another uninstall or native compensation: " + keep + "/" + fault);
            }
            else if (fault is "settings-drift" or "settings-directory")
            {
                f.Host.AfterDisable = null;
                await Denied("settings_drift", () => f.Service.RecoverAsync(f.Operation, default));
                Check(fault == "settings-drift" ? await File.ReadAllTextAsync(f.SettingsPath) == "unrelated settings" : Directory.Exists(f.SettingsPath),
                    "Recovery does not overwrite an unrelated settings file or directory: " + fault);
            }
        }
        using (var f = new Fixture(true))
        {
            await f.SeedAsync(); await f.SeedHostAsync(enabled: false);
            f.Host.UnavailableFailures = 1;
            await Denied("helper_unavailable", () => f.RemoveAsync());
            Check((await f.Store.ReadAsync(default)).Pending is not null && f.Removals == 1,
                "Even disabled preferences require host proof before removal confirmation");
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed && f.Host.Calls == 2,
                "Disabled preference recovery still contacts the ownership-aware host adapter");
        }
        using (var f = new Fixture(false))
        {
            await f.SeedAsync(); await f.SeedHostAsync();
            await File.WriteAllTextAsync(f.SettingsPath, "corrupt");
            await Denied("settings_drift", () => f.RemoveAsync());
            Check(f.Removals == 0 && f.Host.Calls == 0 && (await f.Store.ReadAsync(default)).Pending is null,
                "Unknown settings stop removal before any native or host mutation");
        }
        using (var f = new Fixture(false))
        {
            await f.SeedAsync(); await f.SeedHostAsync();
            var state = await f.Store.ReadAsync(default); var intent = await f.Files.CaptureRemovalAsync(default);
            await Denied("removal_settings_invalid", () => f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan,
                intent with { Candidate = intent.Candidate with { SystemProxyEnabled = true } }, default));
            await Denied("removal_settings_invalid", () => f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan,
                intent with { Candidate = intent.Candidate with { MixedPort = 18889 } }, default));
            await f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan, intent, default);
            var pending = await f.Store.ReadAsync(default);
            var node = JsonNode.Parse(RuntimeJson.Serialize(pending))!;
            node["pending"]!["settings"] = null;
            await f.WriteProtectedAsync(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
            await Denied("publication_state_invalid", () => f.Store.ReadAsync(default));
        }
        using (var f = new Fixture(false))
        {
            await f.SeedAsync(); await f.SeedHostAsync();
            var state = await f.Store.ReadAsync(default); var intent = await f.Files.CaptureRemovalAsync(default);
            await f.Store.BeginRemovalAsync(f.Operation, state.Revision, f.Plan, intent, default);
            f.Commit(true); // Exact crash window after root receipt, host cleanup and settings write, before authority commit.
            await f.Files.CommitRemovalAsync(intent, default);
            f.Recreate();
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.Removals == 0 && f.Host.HttpProxy == MihomoRemovalHostFixture.Original && !(await f.ReadSettingsAsync()).SystemProxyEnabled,
                "Settings-write crash window accepts only the frozen candidate and never repeats native removal");
        }
        return count;
    }

    private sealed class Fixture : IDisposable, IProxyPlatformPaths, IProxyControllerSecretStore, IPrivilegedOperationTransport, IServerModeResolver
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mihomo-removal-publication-" + Guid.NewGuid().ToString("N"));
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal Guid Operation { get; } = Guid.NewGuid();
        internal string Mode = "success";
        internal int Removals, Recoveries;
        internal MihomoRemovalHostFixture Host { get; }
        internal MihomoSettingsPublicationFiles Files => new(this, Host);
        internal string SettingsPath => Path.Combine(GetStateDirectory(), "mihomo-settings.json");
        internal bool GateHeld;
        internal string Active => Path.Combine(GetProtectedConfigurationDirectory(), "active.yaml");
        internal MihomoPublishedRuntime Material { get; } = MihomoConfigurationPolicy.Describe(
            "mixed-port: 18888\nexternal-controller: 127.0.0.1:19090\nsecret: removal-publication-secret\nrules:\n  - MATCH,DIRECT\n").Material;
        internal RuntimeChangePlan Plan { get; }
        internal RuntimeInstallationSnapshot Snapshot { get; private set; }
        internal MihomoPublicationStore Store { get; private set; } = null!;
        internal MihomoRuntimeAuthorizationProvider Authorization { get; private set; } = null!;
        internal MihomoRemovalPublicationService Service { get; private set; } = null!;
        internal MihomoConfigurationPublicationService Configuration { get; private set; } = null!;
        internal MihomoInstallationPublicationService Installation { get; private set; } = null!;
        internal MihomoRuntimeRecoveryService Recovery { get; private set; } = null!;
        internal Fixture(bool keep)
        {
            Directory.CreateDirectory(root);
            Host = new(Path.Combine(root, "host"));
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var instance = new RuntimeInstallation(Guid.NewGuid(), "removal-host", "mihomo", 3, RuntimeOwnership.Managed,
                target, "previous-release", false);
            Snapshot = new(instance, "1.19.30", Guid.NewGuid(), MihomoPublicationStore.Identity(Material.Yaml), null, null, null);
            Plan = new(new(instance.InstallationId, instance.HostId, RuntimeChange.Uninstall, 3, 0, 0, null, "frozen-removal", KeepData: keep),
                "mihomo", null, "previous-release");
            Recreate();
        }
        internal void Recreate()
        {
            Store = new(this, protection); Authorization = new(Store, this, this);
            var runtime = new MihomoRuntimeService(this, Store, Authorization, this, new DirectDownloads());
            Service = new(Store, runtime, this, Files); Configuration = new(Store, Authorization, runtime, this);
            Installation = new(Store, Authorization, runtime, this);
            Recovery = new(Store, runtime, Configuration, Installation, Service);
        }
        internal async Task<ProxySettingsDto> SeedHostAsync(bool enabled = true)
        {
            var settings = new ProxySettingsDto(enabled, false, true, false, true, "info", 18888, true, "127.0.0.1",
                ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default with { BypassList = "keep.example" });
            await Host.SeedAsync(this, settings);
            return settings;
        }
        internal async Task<ProxySettingsDto> ReadSettingsAsync() => JsonSerializer.Deserialize<ProxySettingsDto>(await File.ReadAllBytesAsync(SettingsPath))!;
        internal void AdvanceRevision() => Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = Snapshot.Installation.StateRevision + 1 } };
        internal Task WriteProtectedAsync(byte[] plain) => File.WriteAllBytesAsync(Path.Combine(GetStateDirectory(), "mihomo-publication.protected"),
            protection.CreateProtector("RelaxKonOS.Mihomo.Publication.v1").Protect(plain));
        internal async Task SeedAsync()
        {
            var seed = Guid.NewGuid(); await Store.BeginAsync(seed, 0, Material, default);
            Directory.CreateDirectory(Path.GetDirectoryName(Active)!); await File.WriteAllTextAsync(Active, Material.Yaml);
            await Store.ConfirmAsync(seed, Snapshot.ConfigurationIdentity!, default);
        }
        internal Task<MihomoPublicationResult> RemoveAsync() => Service.RemoveAsync(Operation, Plan, default);
        internal void Commit(bool success)
        {
            Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = 7, RecoveryRequired = false,
                Ownership = success ? RuntimeOwnership.Absent : RuntimeOwnership.Managed,
                ActiveReleaseId = success ? null : "previous-release" }, PendingOperationId = null,
                ActiveRevisionId = success ? null : Snapshot.ActiveRevisionId, MeasuredVersion = success ? null : "1.19.30",
                ConfigurationIdentity = success && !Plan.Request.KeepData ? null : MihomoPublicationStore.Identity(Material.Yaml),
                Receipt = new(Operation, success ? RuntimeSwitchOutcome.Succeeded : RuntimeSwitchOutcome.Interrupted,
                    success ? null : "runtime.operation_interrupted") };
        }
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery) return Json(Snapshot);
            if (Store.Gate.CurrentCount != 0) throw new Exception("Removal did not hold the publication gate");
            GateHeld = true;
            var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(request.RuntimeLifecycle!.Authorization!, request.OperationId!.Value, ct);
            if (grant.Published?.Yaml != Material.Yaml) throw new Exception("Removal did not use the confirmed material");
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                Recoveries++;
                if (Snapshot.PendingOperationId is null) return Json<RuntimeSwitchResult?>(null);
                Commit(false); return Json(Snapshot.Receipt);
            }
            if (request.Operation != PrivilegedOperationKind.MihomoRuntimeExecute) throw new Exception("Unexpected removal operation");
            Removals++;
            if (Mode == "recovery")
            {
                Snapshot = Snapshot with { PendingOperationId = Operation, Installation = Snapshot.Installation with { RecoveryRequired = true } };
                return Json(new RuntimeSwitchResult(Operation, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required"));
            }
            Commit(Mode != "failed");
            if (Mode == "wrong-identity") Snapshot = Snapshot with { ConfigurationIdentity = new string('0', 64) };
            if (Mode == "wrong-instance") Snapshot = Snapshot with { Installation = Snapshot.Installation with { HostId = "another-host" } };
            if (Mode == "wrong-revision") Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = 3 } };
            if (Mode == "mirror-drift") await File.WriteAllTextAsync(Active, "# unrelated configuration");
            if (Mode == "mirror-directory") { File.Delete(Active); Directory.CreateDirectory(Active); }
            if (Mode == "lost-response") throw new IOException("Simulated lost removal receipt");
            return Json(Snapshot.Receipt);
        }
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult<string?>(Material.ControllerSecret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Removal generated credentials");
        public ServerMode ModeValue => ServerMode.System;
        ServerMode IServerModeResolver.Mode => ModeValue;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public string GetEngineVersionsDirectory(string id) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string id) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public void Dispose() => Directory.Delete(root, true);
    }
}
