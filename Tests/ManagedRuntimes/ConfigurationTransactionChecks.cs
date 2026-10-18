using System.Diagnostics;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ConfigurationTransactionChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Refused(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected configuration refusal: " + code);
        }

        using (var f = new Fixture())
        {
            var before = f.Store.Read()!;
            var stages = new List<(InstallationStage, bool)>();
            var engine = new RuntimeArchiveConfiguration(f.Store, f.Adapter, (stage, canCancel) =>
            { stages.Add((stage, canCancel)); return Task.CompletedTask; });
            var result = await engine.ExecuteAsync(f.OperationId, f.Plan, default);
            var after = f.Store.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Succeeded && after.ConfigurationIdentity == f.Plan.CandidateIdentity,
                "Candidate identity committed only with verified activation");
            Check(RuntimeJson.Serialize(after.Active).AsSpan().SequenceEqual(RuntimeJson.Serialize(before.Active))
                && RuntimeJson.Serialize(after.Revisions).AsSpan().SequenceEqual(RuntimeJson.Serialize(before.Revisions)),
                "Configuration publication never creates or changes a program revision");
            Check(after.Configuration is null && after.Receipts.Single().RetainedSnapshot?.ConfigurationSnapshotId == f.OperationId,
                "Receipt retains the operation-scoped snapshot reference");
            Check(stages.SequenceEqual(new[] { (InstallationStage.Preflight, true), (InstallationStage.Preparing, true),
                (InstallationStage.Verifying, true), (InstallationStage.Activating, false), (InstallationStage.HealthChecking, false) }),
                "Progress reports the durable cancellation boundary");
            var calls = f.Adapter.Calls.Count;
            Check(await engine.ExecuteAsync(f.OperationId, f.Plan, default) == result && f.Adapter.Calls.Count == calls,
                "Idempotent replay performs no service or file operation");
            await Refused("idempotency_conflict", () => engine.ExecuteAsync(f.OperationId,
                f.Plan with { CandidateIdentity = new string('d', 64) }, default));
            await Refused("idempotency_conflict", () => engine.ExecuteAsync(Guid.NewGuid(),
                f.Plan with { CandidateIdentity = new string('d', 64) }, default));
        }

        foreach (var failure in new[] { "preflight", "capture", "prepare", "validate", "activate", "verify" })
        {
            using var f = new Fixture();
            f.Adapter.Failure = failure;
            var result = await f.Engine.ExecuteAsync(f.OperationId, f.Plan, default);
            var after = f.Store.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Failed && after.Configuration is null
                && after.ConfigurationIdentity == f.Plan.PreviousIdentity && after.Active!.RevisionId == f.Plan.ActiveRevisionId,
                "Failure at " + failure + " preserves the program and restores configuration");
            Check(f.Adapter.Calls.Contains("restore") == (failure is "activate" or "verify"),
                "Compensation changes live state only after switch: " + failure);
        }

        using (var f = new Fixture())
        using (var cancellation = new CancellationTokenSource())
        {
            f.Adapter.OnValidate = () => cancellation.Cancel();
            var result = await f.Engine.ExecuteAsync(f.OperationId, f.Plan, cancellation.Token);
            Check(result.Outcome == RuntimeSwitchOutcome.Cancelled && !f.Adapter.Calls.Contains("activate")
                && f.Store.Read()!.ConfigurationIdentity == f.Plan.PreviousIdentity, "Cancellation before checkpoint is honored");
        }
        using (var f = new Fixture())
        using (var cancellation = new CancellationTokenSource())
        {
            f.Adapter.OnActivate = () => cancellation.Cancel();
            var result = await f.Engine.ExecuteAsync(f.OperationId, f.Plan, cancellation.Token);
            Check(result.Outcome == RuntimeSwitchOutcome.Succeeded && f.Adapter.ActivationTokenWasUncancelled,
                "Cancellation after checkpoint cannot interrupt switch");
        }

        foreach (var failure in new[] { "stop", "authorize-restore", "restore", "restore-evidence", "cleanup" })
        {
            using var f = new Fixture();
            f.Adapter.Failure = "verify";
            f.Adapter.RecoveryFailure = failure;
            var result = await f.Engine.ExecuteAsync(f.OperationId, f.Plan, default);
            var after = f.Store.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.RecoveryRequired && after.Installation.RecoveryRequired
                && after.Configuration is { Stage: InstallationStage.Interrupted }, "Failed compensation is durable: " + failure);
            var snapshot = RuntimeArchiveRecovery.Describe(after);
            Check(snapshot.Change == RuntimeChange.Configure && snapshot.OperationId == f.OperationId && snapshot.NeedsOperatorDecision
                && snapshot.TargetReleaseId == snapshot.PreviousReleaseId, "Recovery names a configuration-only transaction: " + failure);
            Check(after.ConfigurationIdentity == (failure == "cleanup" ? f.Plan.PreviousIdentity : f.Plan.CandidateIdentity),
                "Only verified restoration changes the durable configuration identity: " + failure);
            await Refused("recovery_required", () => f.Engine.ExecuteAsync(Guid.NewGuid(), f.Plan with { IdempotencyKey = "other" }, default));
            var state = f.Store.Read()!;
            var releasePlan = new RuntimeChangePlan(new(state.Installation.InstallationId, state.Installation.HostId,
                RuntimeChange.Repair, state.Installation.StateRevision, 1, 1, state.Active!.Release.ReleaseId, "blocked-repair"),
                "mihomo", state.Active.Release, state.Active.Release.ReleaseId);
            await Refused("recovery_required", () => new RuntimeArchiveSwitch(f.Store, null!).ExecuteAsync(Guid.NewGuid(), releasePlan, default));
            var removalPlan = releasePlan with { Release = null, Request = releasePlan.Request with
                { Change = RuntimeChange.Uninstall, ReleaseId = null, IdempotencyKey = "blocked-removal" } };
            await Refused("recovery_required", () => new RuntimeArchiveRemoval(f.Store, null!).ExecuteAsync(Guid.NewGuid(), removalPlan, default));
            await Refused("recovery_target_conflict", () => f.Engine.RecoverAsync(Guid.NewGuid(), default));
            if (failure == "authorize-restore") Check(!f.Adapter.Calls.Contains("restore"), "Revoked old configuration is never restarted");
            f.Adapter.RecoveryFailure = null;
            var recovered = await f.Engine.RecoverAsync(f.OperationId, default);
            Check(recovered!.Outcome == RuntimeSwitchOutcome.Interrupted && f.Store.Read()!.ConfigurationIdentity == f.Plan.PreviousIdentity
                && !f.Store.Read()!.Installation.RecoveryRequired, "Recovery retries compensation without preparing: " + failure);
            Check(f.Adapter.Calls.Count(c => c == "prepare") == 1, "Recovery never regenerates credentials or candidate: " + failure);
        }

        using (var f = new Fixture())
        {
            f.Adapter.Failure = "verify"; f.Adapter.RecoveryFailure = "restore";
            await f.Engine.ExecuteAsync(f.OperationId, f.Plan, default);
            using var session = f.Store.Acquire();
            var closed = RuntimeArchiveRecovery.Close(session, f.OperationId, true, new(true, false, false));
            Check(closed.Outcome == RuntimeSwitchOutcome.Failed && session.State!.Configuration is null
                && session.State.ConfigurationIdentity == f.Plan.CandidateIdentity
                && session.State.Active!.RevisionId == f.Plan.ActiveRevisionId,
                "Operator close keeps committed identities without claiming successful restoration");
        }

        foreach (var field in new[] { "revision", "installation", "active", "previous", "digest", "unchanged" })
        {
            using var f = new Fixture();
            var plan = field switch
            {
                "revision" => f.Plan with { ExpectedStateRevision = 99 },
                "installation" => f.Plan with { InstallationId = Guid.NewGuid() },
                "active" => f.Plan with { ActiveRevisionId = Guid.NewGuid() },
                "previous" => f.Plan with { PreviousIdentity = new string('e', 64) },
                "digest" => f.Plan with { CandidateIdentity = "secret-or-path" },
                _ => f.Plan with { CandidateIdentity = f.Plan.PreviousIdentity }
            };
            await Refused(field switch { "revision" or "active" => "state_revision_conflict", "installation" => "installation_mismatch",
                "previous" => "configuration_drift", "digest" => "configuration_invalid", _ => "target_unchanged" },
                () => f.Engine.ExecuteAsync(f.OperationId, plan, default));
            Check(f.Adapter.Calls.Count == 0 && f.Store.Read()!.Configuration is null, "Invalid plan causes no mutation: " + field);
        }

        using (var f = new Fixture())
        {
            using var held = f.Store.Acquire();
            await Refused("instance_busy", () => f.Engine.ExecuteAsync(f.OperationId, f.Plan, default));
            Check(f.Adapter.Calls.Count == 0, "Configuration publication uses the lifecycle instance lock before touching resources");
        }

        using (var f = new Fixture())
        {
            f.Adapter.Failure = "verify"; f.Adapter.RecoveryFailure = "restore";
            await f.Engine.ExecuteAsync(f.OperationId, f.Plan, default);
            using var session = f.Store.Acquire();
            var state = session.State!;
            foreach (var malformed in new[]
            {
                state with { ConfigurationIdentity = f.Plan.PreviousIdentity },
                state with { Configuration = state.Configuration! with { Snapshot = null } },
                state with { Configuration = state.Configuration! with { Plan = f.Plan with { ActiveRevisionId = Guid.NewGuid() } } },
                state with { Configuration = state.Configuration! with { Plan = f.Plan with { CandidateIdentity = "raw-secret" } } },
                state with { Configuration = state.Configuration! with { PreviousRestored = true } }
            })
            {
                await Refused("transaction_state_invalid", () =>
                {
                    session.Save(malformed with { Installation = malformed.Installation with { StateRevision = state.Installation.StateRevision + 1 } });
                    return Task.CompletedTask;
                });
            }
            Check(f.Store.Read()!.ConfigurationIdentity == f.Plan.CandidateIdentity,
                "Invalid journal writes never replace the durable checkpoint");
        }

        // A real process exits at the service activation boundary, with no catch/finally compensation.
        using (var f = new Fixture())
        {
            var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            child.ArgumentList.Add("runtime-configuration-crash"); child.ArgumentList.Add(f.Root); child.ArgumentList.Add(f.OperationId.ToString());
            using var process = Process.Start(child)!;
            await process.WaitForExitAsync();
            Check(process.ExitCode == 77 && f.Store.Read()!.Configuration is { SwitchStarted: true }, "Abrupt process exit leaves a recoverable checkpoint");
            var recovered = await f.Engine.RecoverAsync(f.OperationId, default);
            Check(recovered!.Outcome == RuntimeSwitchOutcome.Interrupted && f.Adapter.Calls.SequenceEqual(
                new[] { "stop", "authorize-restore", "restore", "cleanup" }), "Restart compensates without replaying preparation or activation");
        }
        return count;
    }

    internal static async Task CrashAsync(string root, Guid operationId)
    {
        var store = new RuntimeArchiveStateStore(Path.Combine(root, "state"), path => Directory.CreateDirectory(path));
        var adapter = new Adapter { OnActivate = () => Environment.Exit(77) };
        await new RuntimeArchiveConfiguration(store, adapter).ExecuteAsync(operationId, Plan(store.Read()!), default);
        throw new Exception("Crash boundary was not reached");
    }

    private static RuntimeConfigurationPlan Plan(RuntimeArchiveState state) => new(state.Installation.InstallationId,
        state.Installation.HostId, state.Installation.RuntimeId, state.Installation.StateRevision, state.Active!.RevisionId,
        state.ConfigurationIdentity!, new string('b', 64), "configuration-publication");

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "runtime-config-" + Guid.NewGuid().ToString("N"));
        internal Guid OperationId { get; } = Guid.NewGuid();
        internal RuntimeArchiveStateStore Store { get; }
        internal Adapter Adapter { get; } = new();
        internal RuntimeConfigurationPlan Plan { get; }
        internal RuntimeArchiveConfiguration Engine => new(Store, Adapter);
        internal Fixture()
        {
            Store = new(Path.Combine(Root, "state"), path => Directory.CreateDirectory(path));
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var initial = Store.Initialize(new(Guid.NewGuid(), "test-host", "mihomo", 0, RuntimeOwnership.Absent, target, null, false), new string('a', 64));
            var artifact = new RuntimeArtifact("test", new string('c', 64), 4096, "gz", "mihomo", "signed-manifest:key",
                new(4096, 1, [new("mihomo", 16, new string('d', 64), true)]));
            var revision = new RuntimeStoredRevision(Guid.NewGuid(), new("test-release", "mihomo", "v1.19.30", "1.19.30",
                RuntimeChannel.Stable, "test-source", target, artifact, "mihomo-v1", true, RuntimeTrust.Confirmed,
                RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow), "1.19.30", initial.ConfigurationIdentity!, 1, 1);
            using var session = Store.Acquire();
            session.Save(initial with { Active = revision, Revisions = [revision], Installation = initial.Installation with
                { StateRevision = 1, Ownership = RuntimeOwnership.Managed, ActiveReleaseId = revision.Release.ReleaseId } });
            Plan = ConfigurationTransactionChecks.Plan(session.State!);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Adapter : IRuntimeArchiveConfigurationAdapter
    {
        internal List<string> Calls { get; } = [];
        internal string? Failure { get; set; }
        internal string? RecoveryFailure { get; set; }
        internal Action? OnValidate { get; set; }
        internal Action? OnActivate { get; set; }
        internal bool ActivationTokenWasUncancelled { get; private set; }
        private Task Call(string name)
        {
            Calls.Add(name);
            if (Failure == name || RecoveryFailure == name) throw new RuntimePolicyException("runtime.test_failure");
            return Task.CompletedTask;
        }
        public Task PreflightAsync(RuntimeConfigurationPlan plan, RuntimeArchiveState current, CancellationToken ct) => Call("preflight");
        public async Task<RuntimeServiceSnapshot> CaptureAsync(Guid id, RuntimeArchiveState current, CancellationToken ct)
        { await Call("capture"); return new(true, false, current.ConfigurationIdentity, id, id); }
        public Task PrepareAsync(RuntimePendingConfiguration pending, CancellationToken ct) => Call("prepare");
        public async Task ValidateSwitchAsync(RuntimePendingConfiguration pending, CancellationToken ct) { await Call("validate"); OnValidate?.Invoke(); }
        public async Task ActivateAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        { ActivationTokenWasUncancelled = !ct.CanBeCanceled; await Call("activate"); OnActivate?.Invoke(); }
        public async Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        { await Call("verify"); return new("1.19.30", pending.Plan.CandidateIdentity, true, true, true, false); }
        public Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct) => Call("stop");
        public Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct) => Call("authorize-restore");
        public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        { await Call("restore"); return new("1.19.30", pending.Plan.PreviousIdentity, true, false, true, RecoveryFailure != "restore-evidence"); }
        public Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct) => Call("cleanup");
    }
}
