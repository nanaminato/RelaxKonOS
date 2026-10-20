using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Real reviewed binaries and archive transactions, with an explicitly simulated service host.</summary>
internal static class MihomoArchiveTransactionChecks
{
    internal static async Task<int> RunAsync(string root, RuntimeSourceStore sources, RuntimeCatalog catalog,
        string olderArchive, string newerArchive, string yaml)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Protect(string path)
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var older = catalog.Releases.Single(r => r.SortVersion == "1.19.10");
        var newer = catalog.Releases.Single(r => r.SortVersion == "1.19.30");
        var state = new RuntimeArchiveStateStore(Path.Combine(root, "state"), Protect);
        state.Initialize(new(Guid.NewGuid(), "mihomo-transaction-fixture", "mihomo", 0,
            RuntimeOwnership.Absent, older.Target, null, false), null);
        var grant = new Authorization(yaml);
        var host = new Host();
        var transport = new Transport(new() { [older.ReleaseId] = olderArchive, [newer.ReleaseId] = newerArchive });
        var adapter = new MihomoArchiveAdapter(root, state, sources, transport, downloadProxy: null, host, grant, Protect);
        var transaction = new RuntimeArchiveSwitch(state, adapter);
        RuntimeChangePlan Plan(RuntimeChange action, RuntimeRelease release, string key)
        {
            var current = state.Read()!;
            return new(new(current.Installation.InstallationId, current.Installation.HostId, action,
                current.Installation.StateRevision, catalog.Revision, catalog.TrustRevision, release.ReleaseId, key),
                "mihomo", release, current.Active?.Release.ReleaseId);
        }
        string Data(RuntimeStoredRevision revision) => Path.Combine(root, "data", revision.RevisionId.ToString("N"));
        string Marker(RuntimeStoredRevision revision) => Path.Combine(Data(revision), "selection.db");

        var installId = Guid.NewGuid();
        var installPlan = Plan(RuntimeChange.Install, older, "install");
        var install = await transaction.ExecuteAsync(installId, installPlan, default);
        Check(install.Outcome == RuntimeSwitchOutcome.Succeeded, "Native Mihomo transaction installs: " + install.ProblemCode);
        var original = state.Read()!.Active!;
        Check(original.MeasuredVersion == "1.19.10" && host.Observation.Exists && !host.Observation.Running
            && host.Observation.BootEnabled == false, "Installation registers without starting or enabling on boot");
        Check(host.Observation.DataDirectory == Data(original), "Service registration uses its own data revision");
        var calls = transport.Calls;
        var repeated = await transaction.ExecuteAsync(installId, installPlan, default);
        Check(repeated == install && transport.Calls == calls, "Idempotent replay neither downloads nor configures again");
        host.Observation = host.Observation with { Running = true, BootEnabled = true };
        await File.WriteAllTextAsync(Marker(original), "older-data");
        host.BeforeStop = () => File.WriteAllText(Marker(original), "last-write-before-stop");
        var updated = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "update"), default);
        Check(updated.Outcome == RuntimeSwitchOutcome.Succeeded, "Native cross-version transaction updates: " + updated.ProblemCode);
        var upgraded = state.Read()!.Active!;
        Check(upgraded.MeasuredVersion == "1.19.30" && host.Observation.Running && host.Observation.BootEnabled == true,
            "Cross-version update preserves running and boot state");
        Check(await File.ReadAllTextAsync(Marker(upgraded)) == "last-write-before-stop",
            "Target data includes writes made by the old service immediately before stop");
        await File.WriteAllTextAsync(Marker(upgraded), "new-version-data");
        Check(await File.ReadAllTextAsync(Marker(original)) == "last-write-before-stop", "New version cannot modify the retained old data");

        var repair = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, newer, "repair"), default);
        var repaired = state.Read()!.Active!;
        Check(repair.Outcome == RuntimeSwitchOutcome.Succeeded && repaired.RevisionId != upgraded.RevisionId
            && repaired.MeasuredVersion == upgraded.MeasuredVersion, "Same-version repair creates a new revision without reporting an update");
        Check(await File.ReadAllTextAsync(Marker(repaired)) == "new-version-data", "Repair preserves live runtime data");
        try
        {
            await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "unchanged"), default);
            throw new Exception("Same-version update was accepted");
        }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.target_unchanged") { count++; }
        var downgrade = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, older, "downgrade"), default);
        Check(downgrade.ProblemCode == "runtime.update_target_not_newer" && state.Read()!.Active!.RevisionId == repaired.RevisionId,
            "A lower version requires the rollback action");

        var rollback = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Rollback, older, "rollback"), default);
        var rolledBack = state.Read()!.Active!;
        Check(rollback.Outcome == RuntimeSwitchOutcome.Succeeded && rolledBack.MeasuredVersion == "1.19.10",
            "Explicit rollback native-checks and selects the retained older release");
        Check(await File.ReadAllTextAsync(Marker(rolledBack)) == "last-write-before-stop",
            "Rollback uses the target's retained data instead of the newer database");
        Check(host.Observation.Running && host.Observation.BootEnabled == true, "Rollback preserves independent running and boot flags");

        host.FailConfigure = true;
        var failedId = Guid.NewGuid();
        var failed = await transaction.ExecuteAsync(failedId, Plan(RuntimeChange.Update, newer, "configure-failure"), default);
        Check(failed.Outcome == RuntimeSwitchOutcome.Failed && !state.Read()!.Installation.RecoveryRequired,
            "A failed target registration is compensated");
        Check(state.Read()!.Active!.RevisionId == rolledBack.RevisionId && host.Observation.DataDirectory == Data(rolledBack)
            && host.Observation.Running, "Compensation restores program, data, configuration and running state");
        Check(await File.ReadAllTextAsync(Marker(rolledBack)) == "last-write-before-stop",
            "Writes by a failed target do not enter the restored data");
        Check(!Directory.Exists(Path.Combine(root, "data", failedId.ToString("N")))
            && !Directory.Exists(Path.Combine(root, "versions", failedId.ToString("N"))), "Compensated target data and program are discarded");

        host.FailHealth = true;
        var unhealthy = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "health-failure"), default);
        Check(unhealthy.Outcome == RuntimeSwitchOutcome.Failed && unhealthy.ProblemCode == "runtime.health_check_failed"
            && state.Read()!.Active!.RevisionId == rolledBack.RevisionId && host.Observation.Running,
            "Failed listener health restores the previous native-checked version");

        using (var cancellation = new CancellationTokenSource())
        {
            transport.AfterCopy = cancellation.Cancel;
            var cancelled = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "cancelled"), cancellation.Token);
            Check(cancelled.Outcome == RuntimeSwitchOutcome.Cancelled && state.Read()!.Active!.RevisionId == rolledBack.RevisionId,
                "Download cancellation leaves the running revision unchanged");
            transport.AfterCopy = null;
        }
        var stoppedBefore = host.Stops;
        var downloadsBefore = transport.Calls;
        var publishedBefore = grant.Published;
        grant.Published = publishedBefore with { Yaml = yaml + "\ntun: {enable: true, stack: gvisor}\n", NetworkTransitionRequired = false };
        var hiddenTun = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "hidden-tun"), default);
        Check(hiddenTun.ProblemCode == "runtime.network_transaction_required" && host.Stops == stoppedBefore && transport.Calls == downloadsBefore,
            "Effective YAML cannot hide a TUN change behind a false callback flag");
        grant.Published = publishedBefore with { Yaml = yaml + "\nproxy-providers: {escape: {type: file, path: /etc/shadow}}\n" };
        var escape = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "resource-escape"), default);
        Check(escape.ProblemCode == "runtime.profile_resource_path_forbidden" && host.Stops == stoppedBefore && transport.Calls == downloadsBefore,
            "Unscoped resources are rejected before downloading or stopping the service");
        grant.Published = publishedBefore with { Listeners = [new("0.0.0.0", 17890)] };
        var forgedListener = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "forged-listener"), default);
        Check(forgedListener.ProblemCode == "runtime.authorization_changed" && host.Stops == stoppedBefore && transport.Calls == downloadsBefore,
            "Listener evidence must describe the configuration bytes rather than caller claims");
        grant.Published = publishedBefore;
        grant.Published = grant.Published with { NetworkTransitionRequired = true };
        var network = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "tun-gate"), default);
        Check(network.ProblemCode == "runtime.network_transaction_required" && host.Stops == stoppedBefore,
            "TUN changes require the network transaction before a running service is stopped");
        grant.Published = grant.Published with { NetworkTransitionRequired = false, RequiredBuildTags = ["missing_build_feature"] };
        var missing = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "feature-gate"), default);
        Check(missing.ProblemCode == "runtime.native_feature_missing" && host.Stops == stoppedBefore,
            "A required build feature is measured before switching services");
        grant.Published = grant.Published with { RequiredBuildTags = ["with_gvisor"] };

        var interruptedId = Guid.NewGuid();
        host.AfterStart = () => grant.Published = grant.Published with { Yaml = yaml + "\n# authorization changed\n" };
        var recovery = await transaction.ExecuteAsync(interruptedId, Plan(RuntimeChange.Update, newer, "authorization-change"), default);
        Check(recovery.Outcome == RuntimeSwitchOutcome.RecoveryRequired && state.Read()!.Installation.RecoveryRequired
            && !host.Observation.Running, "Changed publication prevents stale compensation and leaves the service stopped for recovery");
        grant.Published = grant.Published with { Yaml = yaml };
        var recovered = await transaction.RecoverAsync(interruptedId, default);
        Check(recovered is { Outcome: RuntimeSwitchOutcome.Interrupted } && !state.Read()!.Installation.RecoveryRequired
            && state.Read()!.Active!.RevisionId == rolledBack.RevisionId && host.Observation.Running,
            "Named recovery revalidates current authorization and restores the old revision");

        var removal = new RuntimeArchiveRemoval(state, new MihomoRemovalAdapter(root, host, grant, sources, Protect));
        RuntimeChangePlan RemovePlan(string key, bool keepData)
        {
            var current = state.Read()!;
            return new(new(current.Installation.InstallationId, current.Installation.HostId, RuntimeChange.Uninstall,
                current.Installation.StateRevision, 0, 0, null, key, KeepData: keepData), "mihomo", null,
                current.Active!.Release.ReleaseId);
        }
        host.FailRemove = true;
        var removeFailure = await removal.ExecuteAsync(Guid.NewGuid(), RemovePlan("remove-failure", true), default);
        Check(removeFailure.Outcome == RuntimeSwitchOutcome.Failed && host.Observation.Running && !state.Read()!.Installation.RecoveryRequired,
            "Failed service removal validates fresh authorization and restores the old service");
        host.FailRemove = true;
        host.BeforeRemove = () => grant.Published = grant.Published with { Yaml = yaml + "\n# revoked publication\n" };
        var removeRecoveryId = Guid.NewGuid();
        var staleRemove = await removal.ExecuteAsync(removeRecoveryId, RemovePlan("remove-stale-authorization", true), default);
        Check(staleRemove.Outcome == RuntimeSwitchOutcome.RecoveryRequired && !host.Observation.Running,
            "Failed uninstall cannot restart a changed or revoked publication");
        grant.Published = grant.Published with { Yaml = yaml };
        Check((await removal.RecoverAsync(removeRecoveryId, default)) is { Outcome: RuntimeSwitchOutcome.Interrupted }
            && host.Observation.Running && !state.Read()!.Installation.RecoveryRequired,
            "Named removal recovery revalidates authorization before restarting");
        var removed = await removal.ExecuteAsync(Guid.NewGuid(), RemovePlan("retain", true), default);
        Check(removed.Outcome == RuntimeSwitchOutcome.Succeeded && !host.Observation.Exists
            && state.Read()!.Installation.Ownership == RuntimeOwnership.Absent, "Retention uninstall removes the service and committed program ownership");
        Check(File.Exists(Marker(rolledBack)) && File.Exists(Path.Combine(root, "retained-data.json"))
            && !Directory.Exists(Path.Combine(root, "versions")), "Retention uninstall preserves data provenance and removes binaries");
        var reinstall = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Install, older, "reinstall"), default);
        Check(reinstall.Outcome == RuntimeSwitchOutcome.Succeeded && await File.ReadAllTextAsync(Marker(state.Read()!.Active!)) == "last-write-before-stop",
            "Retained data reinstalls without regenerating its contents");
        var latest = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Update, newer, "latest-before-retain"), default);
        Check(latest.Outcome == RuntimeSwitchOutcome.Succeeded, "Reinstalled runtime can cross versions again");
        Check((await removal.ExecuteAsync(Guid.NewGuid(), RemovePlan("retain-newer", true), default)).Outcome == RuntimeSwitchOutcome.Succeeded,
            "The current data provenance replaces an older retained selection");
        var lowerInstall = await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Install, older, "unsafe-retained-downgrade"), default);
        Check(lowerInstall.ProblemCode == "runtime.retained_data_version_unsupported" && !host.Observation.Exists,
            "Reinstall cannot hand retained newer data to an older binary");
        Check((await transaction.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Install, newer, "reinstall-latest"), default)).Outcome == RuntimeSwitchOutcome.Succeeded,
            "Retained newer data still permits same-version reinstall");
        var deleted = await removal.ExecuteAsync(Guid.NewGuid(), RemovePlan("delete", false), default);
        Check(deleted.Outcome == RuntimeSwitchOutcome.Succeeded && !Directory.Exists(Path.Combine(root, "data"))
            && !File.Exists(Path.Combine(root, "retained-data.json")) && !File.Exists(Path.Combine(root, "active.yaml")),
            "Explicit data removal deletes retained revisions, provenance and configuration");
        return count;
    }

    private sealed class Authorization(string yaml) : IMihomoArchiveAuthorization
    {
        internal MihomoPublishedRuntime Published = new(yaml, ["with_gvisor"], 9090, "acceptance-controller-secret", [new("127.0.0.1", 17890)]);
        public Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct) => Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddMinutes(1), Published));
    }

    private sealed class Transport(Dictionary<string, string> archives) : IRuntimeArchiveTransport
    {
        internal int Calls;
        internal Action? AfterCopy;
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, RuntimeDownloadProxy? proxy, Stream destination, CancellationToken ct)
        {
            Calls++;
            await using var input = File.OpenRead(archives[release.ReleaseId]);
            await input.CopyToAsync(destination, ct);
            AfterCopy?.Invoke();
        }
    }

    private sealed class Host : IMihomoArchiveHost
    {
        internal MihomoServiceObservation Observation = new(false, true, false, false, null, null, null);
        internal bool FailConfigure, FailHealth, FailRemove;
        internal int Stops;
        internal Action? BeforeStop, AfterStart, BeforeRemove;
        public Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(Observation);
        public Task<RuntimeServiceObservation> ObserveRegistrationAsync(CancellationToken ct) => Observation.Owned
            ? Task.FromResult(new RuntimeServiceObservation(Observation.Exists, Observation.Running, Observation.BootEnabled))
            : throw new RuntimePolicyException("runtime.external_installation");
        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            var callback = BeforeStop; BeforeStop = null; callback?.Invoke();
            Observation = Observation with { Running = false };
            return Task.CompletedTask;
        }
        public Task ConfigureAsync(string binary, string data, string configuration, string identity, bool boot, CancellationToken ct)
        {
            if (Observation.Running) throw new Exception("Configured a running service");
            if (FailConfigure)
            {
                FailConfigure = false;
                File.WriteAllText(Path.Combine(data, "selection.db"), "failed-target-write");
                throw new IOException("Injected target registration failure");
            }
            Observation = new(true, true, false, boot, binary, identity, data);
            return Task.CompletedTask;
        }
        public Task SetBootAsync(bool enabled, CancellationToken ct) { Observation = Observation with { BootEnabled = enabled }; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken ct)
        {
            Observation = Observation with { Running = true };
            var callback = AfterStart; AfterStart = null; callback?.Invoke();
            return Task.CompletedTask;
        }
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct)
        {
            var callback = BeforeRemove; BeforeRemove = null; callback?.Invoke();
            if (FailRemove) { FailRemove = false; throw new IOException("Injected removal failure"); }
            Observation = new(false, true, false, false, null, null, null);
            return Task.CompletedTask;
        }
        public Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct)
        { var healthy = !FailHealth; FailHealth = false; return Task.FromResult(healthy); }
    }
}
