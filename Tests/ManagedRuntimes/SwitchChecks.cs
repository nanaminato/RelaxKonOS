using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;

internal static class SwitchChecks
{
    internal static async Task RunCrashChildAsync(string root, string phase, Guid operationId)
    {
        using var fixture = new Fixture(root, ownsRoot: false);
        fixture.Adapter.CrashPhase = phase;
        await fixture.Switch.ExecuteAsync(operationId, fixture.Plan(RuntimeChange.Update, "release-2", "crash-" + phase), default);
        throw new Exception("Crash checkpoint was not reached");
    }

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Reject(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected switch rejection: " + code);
        }
        using (var f = new Fixture())
        {
            var operation = Guid.NewGuid(); var plan = f.Plan(RuntimeChange.Install, "release-1", "install");
            Check((await f.Switch.ExecuteAsync(operation, plan, default)).Outcome == RuntimeSwitchOutcome.Succeeded, "First install transaction succeeds");
            var installed = f.Store.Read()!;
            Check(installed.Active!.RevisionId == operation && installed.Installation.Ownership == RuntimeOwnership.Managed
                && installed.Pending is null && installed.Receipts.Length == 1, "Atomic active pointer and terminal receipt committed");
            Check(!f.Service().Running && !f.Service().BootEnabled, "Installation does not implicitly run or enable boot");
            Check((await f.Switch.ExecuteAsync(operation, plan, default)).Outcome == RuntimeSwitchOutcome.Succeeded && f.Adapter.Activations == 1,
                "Retry returns durable receipt without executing again");
            var newOperation = Guid.NewGuid();
            Check((await f.Switch.ExecuteAsync(newOperation, plan, default)).OperationId == operation, "Same idempotency key returns original operation ID");
            await Reject("idempotency_conflict", () => f.Switch.ExecuteAsync(operation,
                plan with { Request = plan.Request with { ExpectedCatalogRevision = 2 } }, default));
            f.SetRunning(true, false);
            Check((await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Update, "release-2", "update"), default)).Outcome
                == RuntimeSwitchOutcome.Succeeded, "Actual distinct-release switch succeeds in adapter fixture");
            Check(f.Service().Version == "2.0.0" && f.Service().Running && !f.Service().BootEnabled,
                "Current running and boot-enabled states are preserved independently");
            var repair = f.Plan(RuntimeChange.Repair, "release-2", "repair");
            var beforeRepair = f.Store.Read()!.Active!.RevisionId;
            Check((await f.Switch.ExecuteAsync(Guid.NewGuid(), repair, default)).Outcome == RuntimeSwitchOutcome.Succeeded
                && f.Store.Read()!.Active!.RevisionId != beforeRepair, "Same-release repair gets a new immutable revision");
            Check((await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Rollback, "release-1", "rollback"), default)).Outcome
                == RuntimeSwitchOutcome.Succeeded && f.Service().Version == "1.0.0", "Retained permitted release can be regenerated and switched back");
            await Reject("rollback_unavailable", () => f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Rollback, "release-3", "unknown-old"), default));
            await Reject("action_unsupported", () => f.Switch.ExecuteAsync(Guid.NewGuid(),
                f.Plan(RuntimeChange.Update, "release-3", "uninstall") with
                { Request = f.Plan(RuntimeChange.Update, "release-3", "uninstall").Request with { Change = RuntimeChange.Uninstall } }, default));
            Check(await f.Switch.RecoverAsync(Guid.NewGuid(), default) is null, "No recovery work after committed transactions");
        }
        foreach (var ownership in new[] { RuntimeOwnership.External, RuntimeOwnership.Unknown })
        {
            using var f = new Fixture(ownership: ownership);
            await Reject(ownership == RuntimeOwnership.External ? "external_installation" : "state_unknown",
                () => f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "install"), default));
            Check(f.Adapter.Preflights == 0 && f.Store.Read()!.Pending is null, "Unknown/external ownership never reaches adapter");
        }
        using (var f = new Fixture())
        {
            var stages = new List<InstallationStage>();
            var cancellable = new List<bool>();
            var observed = new RuntimeArchiveSwitch(f.Store, f.Adapter, (stage, stillCancellable) =>
            {
                Check(f.Store.Read()!.Pending!.Stage == stage, "Progress follows its durable checkpoint: " + stage);
                stages.Add(stage);
                cancellable.Add(stillCancellable);
                if (stage == InstallationStage.Activating) throw new IOException("Fixture progress reader disconnected");
                return Task.CompletedTask;
            });
            var id = Guid.NewGuid(); var plan = f.Plan(RuntimeChange.Install, "release-1", "progress");
            Check((await observed.ExecuteAsync(id, plan, default)).Outcome == RuntimeSwitchOutcome.Succeeded,
                "Losing a progress reader does not undo an accepted activation");
            Check(stages.SequenceEqual(new[] { InstallationStage.Preflight, InstallationStage.Preparing, InstallationStage.Verifying,
                InstallationStage.Activating, InstallationStage.HealthChecking }), "Checkpoint notifications preserve actual phase order");
            Check(cancellable.SequenceEqual(new[] { true, true, true, false, false }),
                "Only the stages before the durable switch checkpoint are advertised as cancellable");
            await observed.ExecuteAsync(id, plan, default);
            Check(stages.Count == 5, "Idempotent receipt replay does not fabricate new progress events");
        }
        using (var f = new Fixture())
        {
            f.Adapter.RejectPreflight = true;
            var result = await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "denied"), default);
            Check(result.Outcome == RuntimeSwitchOutcome.Failed && result.ProblemCode == "runtime.source_disabled"
                && f.Adapter.Captures == 0 && f.Store.Read()!.Active is null, "Preflight rejection cannot change active runtime");
        }
        using (var f = new Fixture())
        {
            await f.InstallAsync();
            var sameVersion = f.Plan(RuntimeChange.Update, "release-2", "same-version");
            await Reject("version_unchanged", () => f.Switch.ExecuteAsync(Guid.NewGuid(), sameVersion with
                { Release = sameVersion.Release! with { OriginalVersion = f.Store.Read()!.Active!.Release.OriginalVersion } }, default));
        }
        using (var f = new Fixture())
        {
            var sawLock = false;
            f.Adapter.OnPreflight = () =>
            {
                try { using var other = f.Store.Acquire(); }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.instance_busy") { sawLock = true; }
                Check(f.Store.Read()!.Pending is not null, "Read snapshot remains available while mutation lock held");
            };
            await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "locked"), default);
            Check(sawLock, "All adapter phases run under shared instance lock");
        }
        using (var f = new Fixture())
        {
            using var cancellation = new CancellationTokenSource();
            f.Adapter.OnPrepare = cancellation.Cancel;
            Check((await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "cancel"), cancellation.Token)).Outcome
                == RuntimeSwitchOutcome.Cancelled && f.Adapter.Activations == 0 && f.Store.Read()!.Revisions.Length == 0,
                "Preparation cancellation cleans candidate and preserves absence");
        }
        using (var f = new Fixture())
        {
            using var cancellation = new CancellationTokenSource();
            f.Adapter.OnActivate = cancellation.Cancel;
            Check((await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "switch"), cancellation.Token)).Outcome
                == RuntimeSwitchOutcome.Succeeded && f.Adapter.SwitchTokensUncancellable, "Cancellation cannot interrupt durable switch boundary");
        }
        foreach (var failure in new[] { "activate", "health", "version", "configuration", "boot", "running", "native", "restore-health", "restore-authorization", "cleanup" })
        {
            using var f = new Fixture(); await f.InstallAsync(); f.SetRunning(true, true);
            var previous = f.Store.Read()!.Active!;
            f.Adapter.Failure = failure;
            var result = await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Update, "release-2", "failure"), default);
            var needsRecovery = failure is "restore-health" or "restore-authorization" or "cleanup";
            Check(result.Outcome == (needsRecovery ? RuntimeSwitchOutcome.RecoveryRequired : RuntimeSwitchOutcome.Failed), "Failure classified: " + failure);
            if (needsRecovery)
            {
                Check(f.Store.Read()!.Installation.RecoveryRequired && f.Store.Read()!.Pending is not null,
                    "Incomplete compensation retains durable pending state: " + failure);
                await Reject("recovery_required", () => f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Update, "release-3", "blocked"), default));
                if (failure == "restore-authorization") Check(!f.Service().Running && f.Adapter.Restores == 0
                    && f.Store.Read()!.Pending!.RecoveryProblemCode == "runtime.credentials_revoked", "Revoked restore is stopped, diagnosed, and never restarted");
                f.Adapter.Failure = null;
                await Reject("recovery_target_conflict", () => f.Switch.RecoverAsync(Guid.NewGuid(), default));
                Check((await f.Switch.RecoverAsync(f.Store.Read()!.Pending!.OperationId, default))!.Outcome == RuntimeSwitchOutcome.Interrupted, "Explicit recovery can finish compensation: " + failure);
            }
            Check(f.Store.Read()!.Active!.RevisionId == previous.RevisionId && f.Service().Version == previous.MeasuredVersion
                && f.Service().Running && f.Service().BootEnabled && f.Store.Read()!.Pending is null,
                "Program/config/service/boot states restored: " + failure);
            Check(f.Store.Read()!.Revisions.All(r => r.Release.ReleaseId != "release-2"), "Rejected candidate is not offered for rollback: " + failure);
        }
        foreach (var phase in new[] { "capture", "prepare", "activate", "verify" })
        {
            using var f = new Fixture(); await f.InstallAsync(); f.SetRunning(true, false);
            var old = f.Store.Read()!.Active!;
            var operationId = Guid.NewGuid();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SwitchChecks).Assembly.Location);
            foreach (var arg in new[] { "runtime-switch-crash", f.Root, phase, operationId.ToString("D") }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(timeout.Token);
            Check(child.ExitCode == 197, "Worker actually terminated at durable checkpoint: " + phase + " " + await errors + await output);
            Check(f.Store.Read()!.Pending!.OperationId == operationId, "Pending journal survives worker termination: " + phase);
            var capturesBefore = f.Adapter.Captures;
            Check((await f.Switch.RecoverAsync(operationId, default))!.Outcome == RuntimeSwitchOutcome.Interrupted, "Interrupted transaction reconciles: " + phase);
            Check(f.Store.Read()!.Active!.RevisionId == old.RevisionId && f.Service().Version == old.MeasuredVersion
                && f.Service().Running && !f.Service().BootEnabled && f.Adapter.Captures == capturesBefore,
                "Recovery restores old effect without new capture/preparation: " + phase);
        }
        using (var f = new Fixture())
        {
            await f.InstallAsync();
            f.PublishConfiguration("newly-published-business-state");
            var publishedIdentity = f.Store.Read()!.ConfigurationIdentity;
            Check(publishedIdentity != f.Store.Read()!.Active!.ConfigurationIdentity,
                "Current configuration publication is independent from immutable program revision");
            f.Adapter.Failure = "health";
            var result = await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Update, "release-2", "published"), default);
            Check(result.Outcome == RuntimeSwitchOutcome.Failed && f.Store.Read()!.ConfigurationIdentity == publishedIdentity
                && f.Service().Configuration == publishedIdentity, "Compensation restores current published configuration, not stale install-time configuration");
        }
        using (var f = new Fixture())
        {
            await f.InstallAsync();
            f.SetConfigurationDrift();
            var before = f.Adapter.Activations;
            var result = await f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Update, "release-2", "drift"), default);
            Check(result.ProblemCode == "runtime.configuration_drift" && f.Adapter.Activations == before,
                "Unknown native configuration is not overwritten during update");
        }
        using (var f = new Fixture())
        {
            await f.InstallAsync();
            var path = Path.Combine(f.Root, "state", "archive-state.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["active"]!["measuredVersion"] = "tampered";
            File.WriteAllText(path, json.ToJsonString());
            await Reject("transaction_state_invalid", () => Task.FromResult(f.Store.Read()));
        }
        using (var f = new Fixture())
        {
            File.WriteAllText(Path.Combine(f.Root, "state", "archive-state.json"), "{broken");
            await Reject("transaction_state_invalid", () => Task.FromResult(f.Store.Read()));
            await Reject("transaction_state_invalid", () => f.Switch.ExecuteAsync(Guid.NewGuid(), f.Plan(RuntimeChange.Install, "release-1", "corrupt"), default));
        }
        return count;
    }

    private sealed record Service(string? Version, string? Configuration, bool Running, bool BootEnabled);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        private readonly bool ownsRoot;
        internal RuntimeArchiveStateStore Store { get; }
        internal TestAdapter Adapter { get; }
        internal RuntimeArchiveSwitch Switch { get; }
        internal Fixture(string? root = null, bool ownsRoot = true, RuntimeOwnership ownership = RuntimeOwnership.Absent)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-switch-" + Guid.NewGuid().ToString("N"));
            this.ownsRoot = ownsRoot;
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "state"), path => Directory.CreateDirectory(path));
            Store.Initialize(new(Guid.Parse("e790617c-bd51-4e39-a291-c871c1697886"), "test-host", "test-runtime", 0, ownership,
                new("test-os", "test-arch", null, null, RuntimeInstallMode.Archive, "test-service"), null, false), null);
            if (!File.Exists(Path.Combine(Root, "service.json"))) WriteService(new(null, null, false, false));
            Adapter = new(this); Switch = new(Store, Adapter);
        }
        internal Service Service() => JsonSerializer.Deserialize<Service>(File.ReadAllText(Path.Combine(Root, "service.json")))!;
        private void WriteService(Service value) => File.WriteAllText(Path.Combine(Root, "service.json"), JsonSerializer.Serialize(value));
        internal void SetRunning(bool running, bool boot) => WriteService(Service() with { Running = running, BootEnabled = boot });
        internal void SetConfigurationDrift() => WriteService(Service() with { Configuration = new string('d', 64) });
        internal void PublishConfiguration(string value)
        {
            using var session = Store.Acquire();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            WriteService(Service() with { Configuration = hash });
            session.Save(session.State! with { ConfigurationIdentity = hash,
                Installation = session.State!.Installation with { StateRevision = session.State.Installation.StateRevision + 1 } });
        }
        internal Task<RuntimeSwitchResult> InstallAsync() => Switch.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Install, "release-1", "initial"), default);
        internal RuntimeChangePlan Plan(RuntimeChange change, string releaseId, string key)
        {
            var state = Store.Read()!;
            var artifact = new RuntimeArtifact("artifact-" + releaseId, new string('a', 64), 1024, "zip", "runtime", "key-1",
                new(1024, 4, [new("runtime", 4, new string('b', 64), true)]));
            var version = releaseId[^1] + ".0.0";
            var release = new RuntimeRelease(releaseId, state.Installation.RuntimeId, version, version, RuntimeChannel.Stable,
                "source-1", state.Installation.Target, artifact, "test-format", true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, null);
            return new(new(state.Installation.InstallationId, state.Installation.HostId, change, state.Installation.StateRevision,
                1, 1, releaseId, key), state.Installation.RuntimeId, release, state.Active?.Release.ReleaseId);
        }
        public void Dispose()
        {
            if (!ownsRoot) return;
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Test cleanup escaped temp root");
            Directory.Delete(Root, true);
        }
        internal sealed class TestAdapter(Fixture fixture) : IRuntimeArchiveSwitchAdapter
        {
            internal string? Failure, CrashPhase;
            internal bool RejectPreflight;
            internal Action? OnPreflight, OnPrepare, OnActivate;
            internal int Preflights, Captures, Activations, Restores;
            internal bool SwitchTokensUncancellable = true;
            public Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct)
            { Preflights++; OnPreflight?.Invoke(); if (RejectPreflight) throw new RuntimePolicyException("runtime.source_disabled"); return Task.CompletedTask; }
            public Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct)
            {
                Captures++;
                var state = fixture.Service();
                Directory.CreateDirectory(Path.Combine(fixture.Root, "backups"));
                File.WriteAllText(Backup(operationId), JsonSerializer.Serialize(state));
                Crash("capture");
                return Task.FromResult(new RuntimeServiceSnapshot(state.Running, state.BootEnabled, state.Configuration,
                    state.Configuration is null ? null : operationId, operationId));
            }
            public Task<RuntimePreparedSwitch> PrepareAsync(Guid operationId, RuntimeChangePlan plan, RuntimeServiceSnapshot before, CancellationToken ct)
            {
                var revision = new RuntimeStoredRevision(operationId, plan.Release!, plan.Release!.OriginalVersion,
                    Hash("published:" + plan.Release.ReleaseId), plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision);
                Directory.CreateDirectory(Revision(operationId));
                File.WriteAllText(Path.Combine(Revision(operationId), "verified-fixture.json"), JsonSerializer.Serialize(revision));
                Crash("prepare"); OnPrepare?.Invoke(); ct.ThrowIfCancellationRequested();
                return Task.FromResult(new RuntimePreparedSwitch(revision, before.Running, before.BootEnabled));
            }
            public Task ValidateSwitchAsync(RuntimePendingSwitch pending, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task ActivateAsync(RuntimePendingSwitch pending, CancellationToken ct)
            {
                SwitchTokensUncancellable &= !ct.CanBeCanceled; Activations++;
                var prepared = pending.Prepared!;
                fixture.WriteService(new(prepared.Revision.MeasuredVersion, prepared.Revision.ConfigurationIdentity,
                    prepared.RunAfterSwitch, prepared.BootEnabledAfterSwitch));
                Crash("activate"); OnActivate?.Invoke();
                if (Failure == "activate") throw new IOException("Simulated activation failure");
                return Task.CompletedTask;
            }
            public Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingSwitch pending, CancellationToken ct)
            {
                SwitchTokensUncancellable &= !ct.CanBeCanceled; Crash("verify");
                var state = fixture.Service();
                return Task.FromResult(new RuntimeActivationEvidence(Failure == "version" ? "wrong" : state.Version!,
                    Failure == "configuration" ? new string('0', 64) : state.Configuration!, Failure != "native",
                    Failure is null or "version" or "configuration" or "boot" or "running" or "native",
                    Failure == "running" ? !state.Running : state.Running, Failure == "boot" ? !state.BootEnabled : state.BootEnabled));
            }
            public Task StopForRecoveryAsync(RuntimePendingSwitch pending, CancellationToken ct)
            { fixture.SetRunning(false, fixture.Service().BootEnabled); return Task.CompletedTask; }
            public Task ValidateRestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
            { if (Failure == "restore-authorization") throw new RuntimePolicyException("runtime.credentials_revoked"); return Task.CompletedTask; }
            public Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingSwitch pending, CancellationToken ct)
            {
                Restores++;
                var old = JsonSerializer.Deserialize<Service>(File.ReadAllText(Backup(pending.OperationId)))!;
                fixture.WriteService(old);
                return Task.FromResult(new RuntimeRestorationEvidence(old.Version, old.Configuration, old.Running, old.BootEnabled,
                    true, Failure != "restore-health"));
            }
            public Task CleanupPreparationAsync(RuntimePendingSwitch pending, CancellationToken ct)
            {
                if (Failure == "cleanup") throw new IOException("Simulated cleanup failure");
                var path = Path.GetFullPath(Revision(pending.OperationId));
                if (Path.GetDirectoryName(path) != Path.Combine(fixture.Root, "revisions")) throw new Exception("Cleanup escaped revisions root");
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return Task.CompletedTask;
            }
            private string Revision(Guid id) => Path.Combine(fixture.Root, "revisions", id.ToString("N"));
            private string Backup(Guid id) => Path.Combine(fixture.Root, "backups", id.ToString("N") + ".json");
            private void Crash(string phase) { if (CrashPhase == phase) Environment.Exit(197); }
            private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
