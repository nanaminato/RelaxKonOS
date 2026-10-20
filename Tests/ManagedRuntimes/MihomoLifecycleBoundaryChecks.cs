using System.Text.Json;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

internal static class MihomoLifecycleBoundaryChecks
{
    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-boundary-" + Guid.NewGuid().ToString("N"));
        void Protect(string path) => Directory.CreateDirectory(path);
        var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), Protect);
        var host = new Host();
        var lifecycle = new MihomoRuntimeLifecycle(root, states, new(Path.Combine(root, "sources"), Protect), host, Protect);
        var created = 0;
        MihomoRuntimeLifecycle Create() { created++; return lifecycle; }
        var id = Guid.NewGuid();
        var channel = new RuntimeAuthorizationChannel(Guid.NewGuid(), id, "mihomo", new string('0', 64));
        var query = new PrivilegedOperationRequest(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: id);
        var uninstall = new RuntimeChangePlan(new(Guid.NewGuid(), "host", RuntimeChange.Uninstall, 0, 0, 0, null, "key"), "mihomo", null, null);
        var configuration = new RuntimeConfigurationPlan(Guid.NewGuid(), "host", "mihomo", 0, Guid.NewGuid(), new string('a', 64), new string('b', 64), "configuration");
        try
        {
            foreach (var invalid in new[]
            {
                query with { Path = "/host-file" }, query with { RuntimeSourceId = "source" },
                query with { RuntimeLifecycle = new() }, query with { OperationId = Guid.Empty }, query with { OperationId = null },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimePlan, RuntimeLifecycle = new(SourceId: "../other") },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel, FrozenPlan: uninstall with { RuntimeId = "sing-box" }) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel with { RuntimeId = "sing-box" }, FrozenPlan: uninstall) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel with { OperationId = Guid.NewGuid() }, FrozenPlan: uninstall) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel, FrozenPlan: uninstall, Confirmed: true) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeRecoveryClose, RuntimeLifecycle = new(RecoveryOperationId: id) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeCancel, RuntimeLifecycle = new(CancelOperationId: id, Confirmed: true) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeConfigure, RuntimeLifecycle = new(ConfigurationPlan: configuration) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeConfigure, RuntimeLifecycle = new(Authorization: channel, ConfigurationPlan: configuration with { RuntimeId = "sing-box" }) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeConfigure, RuntimeLifecycle = new(Authorization: channel, ConfigurationPlan: configuration, Plan: uninstall.Request) },
                query with { Operation = PrivilegedOperationKind.MihomoRuntimeExecute, RuntimeLifecycle = new(Authorization: channel, ConfigurationPlan: configuration, FrozenPlan: uninstall) },
            }) Check((await MihomoRuntimeOperations.ExecuteAsync(invalid, Create)).ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "Mihomo rejects mixed, wrong-runtime or unconfirmed privileged requests");
            Check(created == 0 && !Directory.Exists(root), "Invalid requests never construct a lifecycle or touch runtime state");
            var result = await MihomoRuntimeOperations.ExecuteAsync(query, Create);
            var snapshot = JsonSerializer.Deserialize<RuntimeInstallationSnapshot>(Convert.FromBase64String(result.OutputBase64!))!;
            Check(result.Success && snapshot.Installation.Ownership == RuntimeOwnership.Absent && created == 1,
                "Valid query records a host-bound observed absent installation");
            host.Unavailable = true;
            var unknownStates = new RuntimeArchiveStateStore(Path.Combine(root, "unknown", "state"), Protect);
            var unavailable = new MihomoRuntimeLifecycle(Path.Combine(root, "unknown"), unknownStates,
                new(Path.Combine(root, "unknown-sources"), Protect), host, Protect);
            var failed = await MihomoRuntimeOperations.ExecuteAsync(query, () => unavailable);
            Check(!failed.Success && failed.Error == "runtime.state_unknown" && unknownStates.Read() is null,
                "Failed service observation never becomes an absent installation");
            host.Unavailable = false;
            var beforeObservation = states.Read()!;
            host.Observation = new(true, false, false, false, "/outside/mihomo", null, "/outside/data");
            var external = await lifecycle.QueryAsync(default);
            Check(external.Installation.Ownership == RuntimeOwnership.External && external.ProblemCode == "runtime.external_installation"
                && external.MeasuredVersion is null, "An external service appearing after initialization is not reported as absent");
            Check(states.Read()!.Installation.StateRevision == beforeObservation.Installation.StateRevision,
                "Read-only queries do not rewrite ownership or state revisions");
            var externalPlan = new RuntimePlanRequest(beforeObservation.Installation.InstallationId, beforeObservation.Installation.HostId,
                RuntimeChange.Install, beforeObservation.Installation.StateRevision, 1, 1, "release", "external-plan");
            Check((await lifecycle.PlanAsync("missing-source", externalPlan, default)).Problems.Contains("runtime.external_installation")
                && states.Read()!.Installation.Ownership == RuntimeOwnership.External,
                "Planning reobserves external ownership under the lock before reading a release source");
            host.Observation = new(false, true, false, false, null, null, null);
            var removedExternal = await lifecycle.QueryAsync(default);
            Check(removedExternal.Installation.Ownership == RuntimeOwnership.Absent,
                "Removing the external registration restores a measured absent observation");
            var absentPlan = await lifecycle.PlanAsync("ignored-removal", externalPlan with { Change = RuntimeChange.Uninstall,
                ReleaseId = null, ExpectedStateRevision = removedExternal.Installation.StateRevision }, default);
            Check(absentPlan.Problems.Contains("runtime.state_revision_conflict") && states.Read()!.Installation.Ownership == RuntimeOwnership.Absent,
                "Changed non-managed ownership advances the revision and invalidates the old plan");

            var revisionId = Guid.NewGuid();
            var binary = Path.Combine(root, "versions", revisionId.ToString("N"), "mihomo");
            var data = Path.Combine(root, "data", revisionId.ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); Directory.CreateDirectory(data);
            var binaryBytes = Encoding.UTF8.GetBytes("fixture-program");
            var yaml = "rules:\n  - MATCH,DIRECT\n";
            await File.WriteAllBytesAsync(binary, binaryBytes);
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(root, "active.yaml"), yaml);
            var identity = MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(yaml));
            using (var session = states.Acquire())
            {
                var state = session.State!;
                var release = new RuntimeRelease("observed-release", "mihomo", "v1.19.30", "1.19.30", RuntimeChannel.Stable, "fixture-source",
                    state.Installation.Target, new("fixture", new string('a', 64), 4096, "gz", "mihomo", "signed-manifest:fixture",
                        new(4096, 1, [new("mihomo", binaryBytes.Length, MihomoArchiveAdapter.Hash(binaryBytes), true)])),
                    "mihomo-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow);
                var revision = new RuntimeStoredRevision(revisionId, release, "1.19.30", identity, 1, 1);
                session.Save(state with { Active = revision, Revisions = [revision], ConfigurationIdentity = identity,
                    Installation = state.Installation with { StateRevision = state.Installation.StateRevision + 1,
                        Ownership = RuntimeOwnership.Managed, ActiveReleaseId = release.ReleaseId } });
            }
            host.Observation = new(true, true, false, false, binary, null, data);
            var managed = await lifecycle.QueryAsync(default);
            Check(managed.Installation.Ownership == RuntimeOwnership.Managed && managed.MeasuredVersion == "1.19.30" && managed.ActiveRevisionId == revisionId,
                "An owned stopped registration with matching immutable bytes and configuration remains managed");
            var changedBytes = binaryBytes.ToArray(); changedBytes[0] ^= 1;
            await File.WriteAllBytesAsync(binary, changedBytes);
            var drift = await lifecycle.QueryAsync(default);
            Check(drift.Installation.Ownership == RuntimeOwnership.Unknown && drift.MeasuredVersion is null
                && drift.ProblemCode == "runtime.installed_file_drift", "Binary drift is unknown rather than a stale measured version");
            await File.WriteAllBytesAsync(binary, binaryBytes);
            await File.WriteAllTextAsync(Path.Combine(root, "active.yaml"), "# drift\n");
            Check((await lifecycle.QueryAsync(default)) is { Installation.Ownership: RuntimeOwnership.Unknown, ProblemCode: "runtime.configuration_drift" },
                "Configuration drift is reported without claiming a verified installation");
            await File.WriteAllTextAsync(Path.Combine(root, "active.yaml"), yaml);
            host.Observation = host.Observation with { Running = true, AppliedConfigurationIdentity = new string('0', 64) };
            Check((await lifecycle.QueryAsync(default)).ProblemCode == "runtime.configuration_drift", "A running process must prove the same applied configuration identity");
            host.Observation = host.Observation with { Running = false, BootEnabled = null };
            Check((await lifecycle.QueryAsync(default)).Installation.Ownership == RuntimeOwnership.Unknown, "Unknown boot state is never guessed");
            host.Observation = host.Observation with { Exists = false, BootEnabled = false };
            Check((await lifecycle.QueryAsync(default)).Installation.Ownership == RuntimeOwnership.Unknown, "Missing owned registration is not misreported as uninstalled");
            host.Observation = host.Observation with { Exists = true };
            File.Delete(binary);
            await NativeRuntimeProcess.RunAsync("/usr/bin/mkfifo", [binary], 128);
            Check((await lifecycle.QueryAsync(default).WaitAsync(TimeSpan.FromSeconds(5))).ProblemCode == "runtime.installed_file_drift",
                "A FIFO replacing the binary is refused before opening it for hashing");
            File.Delete(binary); await File.WriteAllBytesAsync(binary, binaryBytes);
            host.Unavailable = true;
            Check((await lifecycle.QueryAsync(default)).Installation.Ownership == RuntimeOwnership.Unknown,
                "Host observation failure after initialization does not reuse a stale managed observation");
            host.Unavailable = false;
            Check(states.Read()!.Installation.StateRevision == managed.Installation.StateRevision,
                "Drift queries do not mutate the protected program ledger or claim an automatic repair");
            var recoveryId = Guid.NewGuid();
            using (var session = states.Acquire())
            {
                var state = session.State!;
                var plan = new RuntimeConfigurationPlan(state.Installation.InstallationId, state.Installation.HostId, "mihomo",
                    state.Installation.StateRevision, revisionId, identity, new string('e', 64), "emergency-scope");
                session.Save(state with { Installation = state.Installation with { StateRevision = state.Installation.StateRevision + 1, RecoveryRequired = true },
                    Configuration = new(recoveryId, new string('a', 64), new string('b', 64), plan,
                        RelaxKonOS.Protocol.Installations.InstallationStage.Interrupted, new(true, true, identity, recoveryId, recoveryId),
                        false, false, "runtime.health_check_failed", "runtime.credentials_revoked") });
            }
            host.Observation = host.Observation with { Running = true, BootEnabled = true };
            host.Unavailable = true; host.AllowRecoveryMutation = true;
            Check((await lifecycle.RecoveryAsync(default)) is { Service.Running: true, ServiceProblem: "runtime.state_unknown" },
                "Recovery retains the program-integrity problem while reporting the owned unit's non-stopped state");
            Check((await lifecycle.StopAsync(recoveryId, default)) is { Service.Running: false, Service.BootEnabled: false }
                && states.Read()!.Configuration is not null, "Emergency stop needs owned unit scope, not a healthy program or current credential");
            Check((await lifecycle.CloseAsync(recoveryId, default)).Outcome == RuntimeSwitchOutcome.Failed
                && (await lifecycle.QueryAsync(default)).Installation.Ownership == RuntimeOwnership.Unknown,
                "Confirmed operator close proves a stopped unit without inventing healthy program evidence");
            host.Unavailable = false;
            var cancellation = await MihomoRuntimeOperations.ExecuteAsync(query with { Operation = PrivilegedOperationKind.MihomoRuntimeCancel,
                RuntimeLifecycle = new(CancelOperationId: id) }, Create);
            Check(cancellation.Success && RuntimeCancelChannel.Matches(root, id), "Dedicated cancellation records only its named operation request");
            Check(states.Read()!.Pending is null && !states.Read()!.Installation.RecoveryRequired,
                "A cancellation request does not manufacture a transaction outcome");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Mihomo boundary cleanup escaped its root");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return count;
    }

    private sealed class Host : IMihomoArchiveHost
    {
        internal bool Unavailable;
        internal bool AllowRecoveryMutation;
        internal MihomoServiceObservation Observation = new(false, true, false, false, null, null, null);
        public Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct) => Unavailable
            ? throw new RuntimePolicyException("runtime.state_unknown") : Task.FromResult(Observation);
        public Task<RuntimeServiceObservation> ObserveRegistrationAsync(CancellationToken ct) => Observation.Owned
            ? Task.FromResult(new RuntimeServiceObservation(Observation.Exists, Observation.Running, Observation.BootEnabled))
            : throw new RuntimePolicyException("runtime.external_installation");
        public Task StopAsync(CancellationToken ct)
        { if (!AllowRecoveryMutation) throw new Exception("Unexpected service mutation"); Observation = Observation with { Running = false }; return Task.CompletedTask; }
        public Task ConfigureAsync(string binary, string data, string configuration, string identity, bool boot, CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task SetBootAsync(bool enabled, CancellationToken ct)
        { if (!AllowRecoveryMutation) throw new Exception("Unexpected service mutation"); Observation = Observation with { BootEnabled = enabled }; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct) => throw new Exception("Unexpected health mutation");
    }
}
