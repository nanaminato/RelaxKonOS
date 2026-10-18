using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Operator recovery entry: ledger facts, the mandatory stop, and closing a transaction whose compensation
/// already failed. It runs against the real state store and store validation, with no platform service.
/// </summary>
internal static class RecoveryChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        static void Refused(string code, Action action)
        {
            try { action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { return; }
            throw new Exception("Expected " + code);
        }

        // Nothing unfinished is reported as nothing unfinished, not as an empty recovery.
        using (var fixture = new Fixture())
        {
            var snapshot = RuntimeArchiveRecovery.Describe(fixture.States.Read());
            Check(!snapshot.Required && snapshot.OperationId is null && snapshot.Service is null,
                "A settled installation reports no recovery");
            Refused("runtime.recovery_absent", () => RuntimeArchiveRecovery.Require(snapshot, Guid.NewGuid()));
        }

        // A failed compensation is described from the ledger alone, with both problem codes kept apart.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: true);
            var snapshot = RuntimeArchiveRecovery.Describe(fixture.States.Read());
            Check(snapshot is { Required: true, OperationId: not null, Change: RuntimeChange.Update, SwitchStarted: true,
                    ProblemCode: "runtime.switch_failed", RecoveryProblemCode: "runtime.compensation_failed",
                    NeedsOperatorDecision: true },
                "Recovery facts name the transaction, its change, its boundary and both failures");
            Check(snapshot.OperationId == operationId && snapshot.PreviousReleaseId == fixture.Previous.Release.ReleaseId
                && snapshot.TargetReleaseId == fixture.Release.ReleaseId,
                "Recovery facts name the previous and target releases without exposing paths");
            Refused("runtime.recovery_target_conflict", () => RuntimeArchiveRecovery.Require(snapshot, Guid.NewGuid()));
            Check(RuntimeArchiveRecovery.Require(snapshot, operationId).OperationId == operationId,
                "Only the transaction the host holds may be addressed");
        }

        // Closing requires a confirmation, a real stop, and a transaction that already failed compensation.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: true);
            using (var session = fixture.States.Acquire())
            {
                Refused("runtime.recovery_confirmation_required",
                    () => RuntimeArchiveRecovery.Close(session, operationId, confirmed: false, fixture.Stopped));
                Refused("runtime.recovery_stop_required",
                    () => RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, null));
                Refused("runtime.recovery_stop_required",
                    () => RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Running));
                Refused("runtime.recovery_target_conflict",
                    () => RuntimeArchiveRecovery.Close(session, Guid.NewGuid(), confirmed: true, fixture.Stopped));
            }
            Check(fixture.States.Read()!.Installation.RecoveryRequired,
                "A refused close leaves the transaction exactly where it was");
        }

        // A transaction that has not failed its compensation is not closable: that would skip a working retry.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: true, recoveryRequired: false, compensated: false);
            using var session = fixture.States.Acquire();
            Refused("runtime.recovery_not_required",
                () => RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped));
        }

        // After an incomplete compensation and a stop, closing keeps the committed pointer and says so.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: true);
            var before = fixture.States.Read()!;
            RuntimeSwitchResult result;
            using (var session = fixture.States.Acquire())
                result = RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            var after = fixture.States.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Failed
                && result.ProblemCode == RuntimeArchiveRecovery.ClosedProblemCode,
                "Closing records a failed receipt with its own problem code");
            Check(after.Pending is null && !after.Installation.RecoveryRequired
                && after.Active?.RevisionId == before.Active?.RevisionId
                && after.ConfigurationIdentity == before.ConfigurationIdentity
                && after.Installation.ActiveReleaseId == before.Installation.ActiveReleaseId
                && after.Installation.Ownership == RuntimeOwnership.Managed,
                "Closing keeps the committed program and configuration identity instead of inventing a restore");
            Check(after.Receipts.Length == 1 && after.Receipts[0].OperationId == operationId
                && after.Receipts[0].RetainedSnapshot is { Running: true },
                "One receipt records the closed transaction and the state captured before the switch");
            Check(after.Installation.StateRevision > before.Installation.StateRevision,
                "Closing advances the state revision so stale plans cannot apply");
            Check(await fixture.ProbeAsync(operationId) == "runtime.action_unsupported",
                "Closing lifts the recovery block that rejected every lifecycle change");
        }

        // Before the switch boundary the durable pointer is the previous revision, so closing is exact.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: false);
            var before = fixture.States.Read()!;
            using (var session = fixture.States.Acquire())
                RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            var after = fixture.States.Read()!;
            Check(after.Active?.RevisionId == fixture.Previous.RevisionId
                && after.ConfigurationIdentity == fixture.Previous.ConfigurationIdentity
                && after.Active?.RevisionId == before.Active?.RevisionId,
                "Closing before the switch keeps the previous revision and its published configuration");
            Check(after.Revisions.Length == 1 && after.Revisions[0].RevisionId == fixture.Previous.RevisionId,
                "The rejected candidate never becomes a rollback target");
        }

        // A second close has nothing to close; it must not report success.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistSwitch(operationId, switchStarted: true);
            using (var session = fixture.States.Acquire())
                RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            using var again = fixture.States.Acquire();
            Refused("runtime.recovery_absent",
                () => RuntimeArchiveRecovery.Close(again, operationId, confirmed: true, fixture.Stopped));
        }

        // A removal that never started still owns its program and registration, so closing only drops the attempt.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistRemoval(operationId, removalStarted: false, cleanupStarted: false);
            var before = fixture.States.Read()!;
            using (var session = fixture.States.Acquire())
                RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            var after = fixture.States.Read()!;
            Check(after.Removal is null && !after.Installation.RecoveryRequired
                && after.Active?.RevisionId == before.Active?.RevisionId
                && after.Installation.Ownership == RuntimeOwnership.Managed
                && after.Active?.RevisionId == fixture.Previous.RevisionId,
                "Closing a removal before it started keeps the owned installation intact");
        }

        // Once the registration is gone, closing must hand ownership away rather than claim a managed service.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistRemoval(operationId, removalStarted: true, cleanupStarted: false);
            using (var session = fixture.States.Acquire())
                RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            var after = fixture.States.Read()!;
            Check(after.Removal is null && after.Active is null && after.Revisions.Length == 0
                && after.Installation.Ownership == RuntimeOwnership.Absent && after.Installation.ActiveReleaseId is null
                && after.ConfigurationIdentity == fixture.Identity,
                "Closing a started removal disowns the installation and keeps the retained configuration");
        }

        // The same close with an explicit data-deletion request must not claim retained configuration.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistRemoval(operationId, removalStarted: true, cleanupStarted: false, keepData: false);
            using (var session = fixture.States.Acquire())
                RuntimeArchiveRecovery.Close(session, operationId, confirmed: true, fixture.Stopped);
            var after = fixture.States.Read()!;
            Check(after.ConfigurationIdentity is null && after.Installation.Ownership == RuntimeOwnership.Absent,
                "Closing a data-deleting removal records no retained configuration");
        }

        // Description of a removal keeps its own vocabulary and names the release being removed.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            fixture.PersistRemoval(operationId, removalStarted: true, cleanupStarted: false);
            var snapshot = RuntimeArchiveRecovery.Describe(fixture.States.Read());
            Check(snapshot is { Required: true, Change: RuntimeChange.Uninstall, TargetReleaseId: null }
                && snapshot.PreviousReleaseId == fixture.Previous.Release.ReleaseId,
                "Removal recovery reports no target release and keeps the removal vocabulary");
        }

        return count;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "relaxkonos-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Target = new RuntimeTarget(OperatingSystem.IsWindows() ? "windows" : "linux",
                OperatingSystem.IsWindows() ? "amd64" : "arm64", null, null, RuntimeInstallMode.Archive,
                OperatingSystem.IsWindows() ? "scm" : "systemd");
            States = new RuntimeArchiveStateStore(Path.Combine(Root, "state"), path => Directory.CreateDirectory(path));
            Identity = Digest(Encoding.UTF8.GetBytes("{\"log\":{},\"inbounds\":[]}"));
            NewIdentity = Digest(Encoding.UTF8.GetBytes("{\"log\":{},\"inbounds\":[{\"type\":\"mixed\"}]}"));
            var artifact = new RuntimeArtifact("fixture-artifact", new string('c', 64), 4096, "tar.gz", "sing-box", "fixture-key",
                new(1024 * 1024, 8, [new("sing-box", 16, new string('b', 64), true)]));
            Previous = new RuntimeStoredRevision(Guid.NewGuid(),
                new RuntimeRelease("fixture-release-1", "sing-box", "v1.14.3", "1.14.3", RuntimeChannel.Stable, "fixture-source",
                    Target, artifact, "sing-box-1.14.3", true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, DateTimeOffset.UtcNow),
                "1.14.3", Identity, 1, 1);
            Release = new RuntimeRelease("fixture-release-2", "sing-box", "v1.15.0", "1.15.0", RuntimeChannel.Stable, "fixture-source",
                Target, artifact, "sing-box-1.15.0", true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, DateTimeOffset.UtcNow);
            var initial = States.Initialize(new(Guid.NewGuid(), "fixture-host", "sing-box", 0, RuntimeOwnership.Absent,
                Target, null, false), Identity);
            using (var session = States.Acquire())
                session.Save(initial with
                {
                    Installation = initial.Installation with
                    {
                        StateRevision = 1, Ownership = RuntimeOwnership.Managed, ActiveReleaseId = Previous.Release.ReleaseId
                    },
                    Active = Previous,
                    Revisions = [Previous]
                });
        }

        internal string Root { get; }
        internal string Identity { get; }
        internal string NewIdentity { get; }
        internal RuntimeTarget Target { get; }
        internal RuntimeRelease Release { get; }
        internal RuntimeStoredRevision Previous { get; }
        internal RuntimeArchiveStateStore States { get; }

        /// <summary>A stopped service is the only observation that permits closing.</summary>
        internal RuntimeServiceObservation Stopped => new(Exists: true, Running: false, BootEnabled: false);
        internal RuntimeServiceObservation Running => new(Exists: true, Running: true, BootEnabled: true);

        /// <summary>Writes exactly what a failed switch compensation persists, so store validation is the real gate.</summary>
        internal void PersistSwitch(Guid operationId, bool switchStarted, bool recoveryRequired = true, bool compensated = true)
        {
            var prepared = new RuntimeStoredRevision(operationId, Release, Release.OriginalVersion, NewIdentity, 1, 1);
            using var session = States.Acquire();
            var state = session.State!;
            var active = switchStarted ? prepared : Previous;
            var plan = new RuntimeChangePlan(new(state.Installation.InstallationId, state.Installation.HostId, RuntimeChange.Update,
                state.Installation.StateRevision, 1, 1, Release.ReleaseId, "recovery-" + operationId.ToString("N")),
                "sing-box", Release, Previous.Release.ReleaseId);
            session.Save(state with
            {
                Installation = state.Installation with
                {
                    StateRevision = state.Installation.StateRevision + 1,
                    Ownership = RuntimeOwnership.Managed,
                    ActiveReleaseId = active.Release.ReleaseId,
                    RecoveryRequired = recoveryRequired
                },
                Active = active,
                ConfigurationIdentity = active.ConfigurationIdentity,
                Revisions = [Previous, prepared],
                Pending = new(operationId, Hash(RuntimeJson.Serialize(plan)), Hash(Encoding.UTF8.GetBytes(
                        plan.Request.HostId + "\n" + plan.Request.InstallationId + "\n" + plan.Request.IdempotencyKey)),
                    plan, InstallationStage.Interrupted, Previous,
                    new(true, true, Previous.ConfigurationIdentity, Guid.NewGuid(), null), new(prepared, true, true), switchStarted,
                    compensated ? "runtime.switch_failed" : null,
                    recoveryRequired && compensated ? "runtime.compensation_failed" : null)
            });
        }

        internal void PersistRemoval(Guid operationId, bool removalStarted, bool cleanupStarted, bool keepData = true)
        {
            using var session = States.Acquire();
            var state = session.State!;
            var plan = new RuntimeChangePlan(new(state.Installation.InstallationId, state.Installation.HostId, RuntimeChange.Uninstall,
                state.Installation.StateRevision, 1, 1, null, "recovery-" + operationId.ToString("N"), false, keepData),
                "sing-box", null, Previous.Release.ReleaseId);
            session.Save(state with
            {
                Installation = state.Installation with { StateRevision = state.Installation.StateRevision + 1, RecoveryRequired = true },
                Removal = new(operationId, Hash(RuntimeJson.Serialize(plan)), Hash(Encoding.UTF8.GetBytes(
                        plan.Request.HostId + "\n" + plan.Request.InstallationId + "\n" + plan.Request.IdempotencyKey)),
                    plan, InstallationStage.Interrupted, new(true, true, Identity, Guid.NewGuid(), null), removalStarted, cleanupStarted,
                    "runtime.removal_failed", "runtime.compensation_health_failed")
            });
        }

        /// <summary>Proves the recovery block is gone: the next lifecycle change fails on its own merits instead.</summary>
        internal async Task<string> ProbeAsync(Guid operationId)
        {
            var state = States.Read()!;
            // A fresh id: reusing the closed one would only exercise idempotency replay.
            var plan = new RuntimeChangePlan(new(state.Installation.InstallationId, state.Installation.HostId, RuntimeChange.Uninstall,
                state.Installation.StateRevision, 1, 1, null, "probe-" + operationId.ToString("N")), "sing-box", null,
                state.Active?.Release.ReleaseId);
            var probe = Guid.NewGuid();
            try { await new RuntimeArchiveSwitch(States, new UnusedAdapter()).ExecuteAsync(probe, plan, default); }
            catch (RuntimePolicyException ex) { return ex.ProblemCode; }
            return "started";
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { }
        }

        private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>No adapter method may run: the probe stops at plan validation.</summary>
    private sealed class UnusedAdapter : IRuntimeArchiveSwitchAdapter
    {
        public Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct) => throw new NotSupportedException();
        public Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct) => throw new NotSupportedException();
        public Task<RuntimePreparedSwitch> PrepareAsync(Guid operationId, RuntimeChangePlan plan, RuntimeServiceSnapshot before, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateSwitchAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task ActivateAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateRestoreAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task StopForRecoveryAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
        public Task CleanupPreparationAsync(RuntimePendingSwitch pending, CancellationToken ct) => throw new NotSupportedException();
    }
}
