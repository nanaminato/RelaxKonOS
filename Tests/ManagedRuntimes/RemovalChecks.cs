using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Archive removal transaction against the real removal adapter and a recording service host. No platform
/// service is registered; deletion stays inside the protected instance root.
/// </summary>
internal static class RemovalChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }

        // Retained data: service and programs go, the published configuration and its identity stay.
        using (var fixture = new Fixture())
        {
            var before = fixture.States.Read()!;
            var result = await fixture.RemoveAsync(Guid.NewGuid(), keepData: true);
            var after = fixture.States.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Succeeded && result.ProblemCode is null
                && after.Installation.Ownership == RuntimeOwnership.Absent && after.Installation.ActiveReleaseId is null
                && after.Active is null && after.Revisions.Length == 0 && after.Removal is null
                && after.ConfigurationIdentity == before.ConfigurationIdentity
                && File.Exists(Path.Combine(fixture.Root, "config.json"))
                && fixture.Host.Stops == 1 && fixture.Host.Removals == 1,
                "Retained-data removal stops the service, removes its registration and keeps the configuration");
            Check(!Directory.Exists(Path.Combine(fixture.Root, "versions"))
                && !Directory.Exists(Path.Combine(fixture.Root, "staging"))
                && !Directory.Exists(Path.Combine(fixture.Root, "operations")),
                "Owned program, staging and backup directories are deleted");
            Check(File.Exists(fixture.OutsideFile), "Removal never deletes files outside the protected instance root");
            Check(after.Installation.StateRevision > before.Installation.StateRevision
                && after.Receipts.Length == 1 && after.Receipts[0].OperationId == result.OperationId
                && after.Receipts[0].Outcome == RuntimeSwitchOutcome.Succeeded && after.Receipts[0].RetainedSnapshot is { Running: true },
                "One receipt records the removed operation and the captured run state");
            var witness = fixture.States.ReadCommittedWitness(after, after.Receipts[0]);
            Check(witness is { Installation.Ownership: RuntimeOwnership.Absent, ActiveRevisionId: null, MeasuredVersion: null }
                && witness.ConfigurationIdentity == before.ConfigurationIdentity && witness.Installation.StateRevision == after.Installation.StateRevision,
                "Retained-data removal records the removed state and preserved configuration in its immutable proof");

            var replay = await fixture.ReplayAsync(result.OperationId);
            Check(replay.OperationId == result.OperationId && replay.Outcome == RuntimeSwitchOutcome.Succeeded
                && fixture.Host.Removals == 1 && fixture.Host.Stops == 1,
                "Replaying the same operation returns its receipt without touching the host again");

            var again = await fixture.RemoveAsync(Guid.NewGuid(), keepData: true, expectFailure: "runtime.not_managed");
            Check(again.Outcome == RuntimeSwitchOutcome.Failed && fixture.Host.Removals == 1,
                "Removing an installation the host no longer owns is refused");
        }

        // Abandoned retention: the same transaction also removes configuration and secrets.
        using (var fixture = new Fixture())
        {
            var result = await fixture.RemoveAsync(Guid.NewGuid(), keepData: false);
            var after = fixture.States.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Succeeded && after.ConfigurationIdentity is null
                && !File.Exists(Path.Combine(fixture.Root, "config.json"))
                && !File.Exists(Path.Combine(fixture.Root, "config.json.previous")),
                "Explicit data deletion removes the published configuration and its retained copy");
            Check(!Directory.Exists(Path.Combine(fixture.Root, "versions")),
                "Data deletion still leaves no program revision behind");
            Check(fixture.States.ReadCommittedWitness(after, after.Receipts.Single()) is
                { Installation.Ownership: RuntimeOwnership.Absent, ConfigurationIdentity: null, ActiveRevisionId: null, MeasuredVersion: null },
                "Data-deleting removal proves null configuration rather than requiring the previous configuration to remain");
        }

        // A failing registration removal restores the exact previous service and reports failure.
        using (var fixture = new Fixture())
        {
            fixture.Host.FailRemoval = true;
            var before = fixture.States.Read()!;
            var result = await fixture.RemoveAsync(Guid.NewGuid(), keepData: true);
            var after = fixture.States.Read()!;
            Check(result.Outcome == RuntimeSwitchOutcome.Failed && result.ProblemCode == "runtime.removal_failed"
                && after.Installation.Ownership == RuntimeOwnership.Managed && after.Active is not null
                && after.ConfigurationIdentity == before.ConfigurationIdentity && after.Removal is null
                && Directory.Exists(Path.Combine(fixture.Root, "versions")),
                "A failed removal restores the installation and keeps its programs");
            Check(fixture.Host.Observation is { Exists: true, Running: true } && fixture.Host.Configures >= 1
                && fixture.Host.Starts >= 1,
                "Compensation re-registers the recorded program and starts it again when it was running");
        }

        // Startup recovery before irreversible cleanup restores the registration and closes the ledger.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            var plan = fixture.Plan(operationId, keepData: true, key: "recover-before");
            fixture.PersistRemoval(operationId, plan, removalStarted: true, cleanupStarted: false);
            fixture.Host.SimulateRegistrationRemoved();
            var recovered = await fixture.Removal.RecoverAsync(operationId, default);
            var after = fixture.States.Read()!;
            Check(recovered is { Outcome: RuntimeSwitchOutcome.Interrupted }
                && after.Removal is null && after.Installation.Ownership == RuntimeOwnership.Managed
                && after.Active is not null && after.Receipts.Length == 1 && after.Installation.RecoveryRequired == false,
                "Recovery of an interrupted removal restores the installation and records an interrupted receipt");
            Check(fixture.Host.Observation is { Exists: true, Running: true },
                "Recovery re-registers and restarts the service captured before removal");
        }

        // Startup recovery after cleanup cannot restore; it must close the ledger without inventing a service.
        using (var fixture = new Fixture())
        {
            var operationId = Guid.NewGuid();
            var plan = fixture.Plan(operationId, keepData: true, key: "recover-after");
            fixture.PersistRemoval(operationId, plan, removalStarted: true, cleanupStarted: true);
            fixture.Host.SimulateRegistrationRemoved();
            var recovered = await fixture.Removal.RecoverAsync(operationId, default);
            var after = fixture.States.Read()!;
            Check(recovered is { Outcome: RuntimeSwitchOutcome.Interrupted } receipt
                && receipt.ProblemCode == "runtime.removal_cleanup_interrupted"
                && after.Installation.Ownership == RuntimeOwnership.Absent && after.Active is null
                && after.Removal is null && after.Receipts.Length == 1,
                "Recovery after irreversible cleanup closes as interrupted instead of claiming a restore");
        }

        // An external or drifted installation is never removed by this application.
        using (var fixture = new Fixture())
        {
            fixture.Host.Observation = fixture.Host.Observation with { Executable = Path.Combine(fixture.Root, "other-binary") };
            var external = await fixture.RemoveAsync(Guid.NewGuid(), keepData: true, expectFailure: "runtime.external_installation");
            Check(external.Outcome == RuntimeSwitchOutcome.Failed && fixture.Host.Removals == 0
                && fixture.States.Read()!.Installation.Ownership == RuntimeOwnership.Managed,
                "A service running another executable is treated as external and left untouched");

            fixture.Host.Observation = fixture.Host.Observation with { Executable = fixture.Binary };
            await File.WriteAllBytesAsync(fixture.Binary, Encoding.UTF8.GetBytes("tampered program"));
            var drifted = await fixture.RemoveAsync(Guid.NewGuid(), keepData: true, expectFailure: "runtime.installed_file_drift");
            Check(drifted.Outcome == RuntimeSwitchOutcome.Failed && fixture.Host.Removals == 0,
                "A program that no longer matches its immutable revision blocks removal");
        }

        return count;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "relaxkonos-removal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            OutsideFile = Path.Combine(Path.GetTempPath(), "relaxkonos-removal-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(OutsideFile, "unrelated");
            Target = new RuntimeTarget(OperatingSystem.IsWindows() ? "windows" : "linux",
                OperatingSystem.IsWindows() ? "amd64" : "arm64", null, null, RuntimeInstallMode.Archive,
                OperatingSystem.IsWindows() ? "scm" : "systemd");
            States = new RuntimeArchiveStateStore(Path.Combine(Root, "state"), path => Directory.CreateDirectory(path));
            Binary = Path.Combine(Root, "versions", RevisionId.ToString("N"), "sing-box");
            Directory.CreateDirectory(Path.GetDirectoryName(Binary)!);
            File.WriteAllBytes(Binary, Program);
            Directory.CreateDirectory(Path.Combine(Root, "staging", "leftover"));
            File.WriteAllText(Path.Combine(Root, "staging", "leftover", "part"), "partial");
            Directory.CreateDirectory(Path.Combine(Root, "operations", "leftover"));
            File.WriteAllText(Path.Combine(Root, "operations", "leftover", "service.json"), "{}");
            Identity = Digest(Encoding.UTF8.GetBytes("{\"log\":{},\"inbounds\":[]}"));
            File.WriteAllText(Path.Combine(Root, "config.json"), "{\"log\":{},\"inbounds\":[]}");
            File.WriteAllText(Path.Combine(Root, "config.json.previous"), "{\"log\":{},\"inbounds\":[]}");
            var entry = new RuntimeArchiveFile("sing-box", Program.Length, Digest(Program), true);
            var artifact = new RuntimeArtifact("fixture-artifact", new string('c', 64), 4096, "tar.gz", "sing-box",
                "fixture-key", new(1024 * 1024, 8, [entry]));
            Release = new RuntimeRelease("fixture-release", "sing-box", "v1.14.3", "1.14.3", RuntimeChannel.Stable,
                "fixture-source", Target, artifact, SingBoxRuntimeSupport.Capability, true, RuntimeTrust.Confirmed,
                RuntimeEvidence.Unverified, DateTimeOffset.UtcNow);
            var revision = new RuntimeStoredRevision(RevisionId, Release, "1.14.3", Identity, 1, 1);
            var initial = States.Initialize(new(Guid.NewGuid(), "fixture-host", "sing-box", 0, RuntimeOwnership.Absent,
                Target, null, false), Identity);
            using (var session = States.Acquire())
                session.Save(initial with
                {
                    Installation = initial.Installation with
                    {
                        StateRevision = initial.Installation.StateRevision + 1,
                        Ownership = RuntimeOwnership.Managed,
                        ActiveReleaseId = Release.ReleaseId
                    },
                    Active = revision,
                    ConfigurationIdentity = Identity,
                    Revisions = [revision]
                });
            Host = new RemovalHost(new(true, true, true, true, Binary, Identity), Identity);
            Removal = new RuntimeArchiveRemoval(States, new SingBoxRemovalAdapter(Root, Host, path => Directory.CreateDirectory(path)));
        }

        internal string Root { get; }
        internal string OutsideFile { get; }
        internal string Binary { get; }
        internal string Identity { get; }
        internal string LastKey { get; private set; } = "";
        internal RuntimeChangePlan? LastPlan { get; private set; }
        internal RuntimeTarget Target { get; }
        internal RuntimeRelease Release { get; }
        internal RuntimeArchiveStateStore States { get; }
        internal RemovalHost Host { get; }
        internal RuntimeArchiveRemoval Removal { get; }
        private Guid RevisionId { get; } = Guid.NewGuid();
        private static byte[] Program => Encoding.UTF8.GetBytes("fixture program");

        internal RuntimeChangePlan Plan(Guid operationId, bool keepData, string key)
        {
            var state = States.Read()!;
            return new(new(state.Installation.InstallationId, state.Installation.HostId, RuntimeChange.Uninstall,
                state.Installation.StateRevision, 1, 1, null, key, false, keepData), "sing-box", null,
                state.Active?.Release.ReleaseId);
        }

        internal async Task<RuntimeSwitchResult> RemoveAsync(Guid operationId, bool keepData, string? key = null, string? expectFailure = null)
        {
            LastKey = key ?? Guid.NewGuid().ToString("N");
            LastPlan = Plan(operationId, keepData, LastKey);
            try
            {
                var result = await Removal.ExecuteAsync(operationId, LastPlan, default);
                if (expectFailure is not null && result.ProblemCode != expectFailure)
                    throw new Exception($"Expected {expectFailure} but removal returned {result.Outcome}/{result.ProblemCode}");
                return result;
            }
            catch (RuntimePolicyException ex) when (expectFailure == ex.ProblemCode)
            { return new(operationId, RuntimeSwitchOutcome.Failed, ex.ProblemCode); }
        }

        internal Task<RuntimeSwitchResult> ReplayAsync(Guid operationId) =>
            Removal.ExecuteAsync(operationId, LastPlan!, default);

        internal void PersistRemoval(Guid operationId, RuntimeChangePlan plan, bool removalStarted, bool cleanupStarted)
        {
            using var session = States.Acquire();
            var state = session.State!;
            session.Save(state with
            {
                Installation = state.Installation with { StateRevision = state.Installation.StateRevision + 1 },
                Removal = new(operationId, Hash(RuntimeJson.Serialize(plan)),
                    Hash(Encoding.UTF8.GetBytes(plan.Request.HostId + "\n" + plan.Request.InstallationId + "\n" + plan.Request.IdempotencyKey)),
                    plan, InstallationStage.Activating,
                    new(true, true, state.ConfigurationIdentity, operationId, operationId), removalStarted, cleanupStarted, null, null)
            });
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { }
            try { if (File.Exists(OutsideFile)) File.Delete(OutsideFile); } catch (IOException) { }
        }

        private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class RemovalHost(SingBoxServiceObservation observation, string identity) : ISingBoxArchiveHost
    {
        internal SingBoxServiceObservation Observation = observation;
        internal int Stops;
        internal int Starts;
        internal int Removals;
        internal int Configures;
        internal bool FailRemoval;
        private string? applied = identity;

        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(Observation);
        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            Observation = Observation with { Running = false, AppliedConfigurationIdentity = null };
            return Task.CompletedTask;
        }
        public Task ConfigureAsync(string binary, string configuration, string configurationIdentity, bool bootEnabled, CancellationToken ct)
        {
            Configures++;
            applied = configurationIdentity;
            Observation = new(true, true, false, bootEnabled, binary, null);
            return Task.CompletedTask;
        }
        public Task SetBootAsync(bool enabled, CancellationToken ct)
        {
            Observation = Observation with { BootEnabled = enabled };
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken ct)
        {
            Starts++;
            Observation = Observation with { Running = true, AppliedConfigurationIdentity = applied };
            return Task.CompletedTask;
        }
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct)
        {
            Removals++;
            if (FailRemoval) throw new IOException("Fixture registration removal failure");
            SimulateRegistrationRemoved();
            return Task.CompletedTask;
        }
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) =>
            Task.FromResult(true);

        internal void SimulateRegistrationRemoved() => Observation = new(false, true, false, null, null, null);
    }
}
