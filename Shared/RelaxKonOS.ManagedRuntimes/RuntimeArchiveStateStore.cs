using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>One protected per-instance state file is also the atomic active pointer. All mutations share its lock.</summary>
public sealed partial class RuntimeArchiveStateStore(string directory, Action<string> protectDirectory)
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private string StatePath => Path.Combine(directory, "archive-state.json");

    public RuntimeArchiveState Initialize(RuntimeInstallation observation, string? publishedConfigurationIdentity)
    {
        using var session = Acquire();
        if (session.State is { } existing)
        {
            if (existing.Installation.InstallationId != observation.InstallationId || existing.Installation.HostId != observation.HostId
                || existing.Installation.RuntimeId != observation.RuntimeId || existing.Installation.Target != observation.Target)
                throw new RuntimePolicyException("runtime.installation_mismatch");
            return existing;
        }
        if (observation.Ownership == RuntimeOwnership.Managed || observation.ActiveReleaseId is not null || observation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.installation_observation_invalid");
        var initial = new RuntimeArchiveState(observation, null, publishedConfigurationIdentity, [], null, []);
        session.Save(initial);
        return initial;
    }

    /// <summary>Atomic snapshots can be read while a worker owns the mutation lock.</summary>
    public RuntimeArchiveState? Read()
    {
        Prepare();
        return ReadConfirmed();
    }

    public Session Acquire()
    {
        Prepare();
        var path = Path.Combine(directory, "instance.lock");
        RuntimeSourceStore.RequireNoLinks(path);
        FileStream gate;
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { gate = new(path, options); }
        catch (IOException) { throw new RuntimePolicyException("runtime.instance_busy"); }
        try { return new(this, gate, ReadConfirmed()); }
        catch { gate.Dispose(); throw; }
    }

    private RuntimeArchiveState? ReadConfirmed()
    {
        var state = ReadCore();
        // Another process can see the new name before the writer syncs it, or after its sync failed.
        // Both observation and subsequent mutations must establish durability before using that receipt.
        // Older receipts have passed this gate on a writer's acquisition before a newer receipt can be appended.
        if (OperatingSystem.IsLinux() && state?.Receipts.LastOrDefault()?.ConfigurationNetworkIntentIdentity is not null)
        {
            try { SyncDirectory(); }
            catch (IOException) { throw new RuntimePolicyException("runtime.recovery_required"); }
        }
        return state;
    }

    private void Prepare()
    {
        RuntimeSourceStore.RequireNoLinks(directory);
        protectDirectory(directory);
        RuntimeSourceStore.RequireNoLinks(directory);
    }

    private RuntimeArchiveState? ReadCore()
    {
        RuntimeSourceStore.RequireNoLinks(StatePath);
        if (Directory.Exists(StatePath)) throw new RuntimePolicyException("runtime.transaction_state_invalid");
        if (!File.Exists(StatePath)) return null;
        try
        {
            // Replacement writes use a different inode/file; readers retain one complete old or new snapshot.
            using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumBytes) throw new RuntimePolicyException("runtime.transaction_state_invalid");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var state = RuntimeJson.Read<RuntimeArchiveState>(bytes);
            Validate(state);
            return state;
        }
        catch (RuntimePolicyException) { throw new RuntimePolicyException("runtime.transaction_state_invalid"); }
    }

    private void WriteCore(RuntimeArchiveState state)
    {
        Validate(state);
        var bytes = RuntimeJson.Serialize(state);
        if (bytes.Length > MaximumBytes) throw new RuntimePolicyException("runtime.transaction_state_limit");
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".new");
        RuntimeSourceStore.RequireNoLinks(StatePath);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            // A cleanup error must not turn a completed atomic replacement into an ambiguous failed commit.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Validate(RuntimeArchiveState state)
    {
        var installation = state.Installation;
        if (installation is null || installation.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(installation.HostId)
            || !RuntimeValidation.Identifier(installation.RuntimeId) || installation.StateRevision < 0
            || !Enum.IsDefined(installation.Ownership) || installation.Target is null
            || installation.Target.InstallMode != RuntimeInstallMode.Archive || state.Revisions is null || state.Receipts is null
            || state.Revisions.Length > 256 || state.Receipts.Length > 4096
            || state.Revisions.Any(r => r is null || r.RevisionId == Guid.Empty || r.Release is null
                || r.Release.RuntimeId != installation.RuntimeId || r.Release.Target != installation.Target
                || !RuntimeArchiveRules.Valid(r.Release.Artifact) || string.IsNullOrWhiteSpace(r.MeasuredVersion)
                || !Digest(r.ConfigurationIdentity) || r.CatalogRevision < 1 || r.TrustRevision < 1)
            || state.Revisions.Select(r => r.RevisionId).Distinct().Count() != state.Revisions.Length
            || (state.Active is null ? installation.ActiveReleaseId is not null
                : installation.ActiveReleaseId != state.Active.Release.ReleaseId || !state.Revisions.Any(r => r.RevisionId == state.Active.RevisionId))
            || installation.Ownership == RuntimeOwnership.Managed && state.Active is null
            || state.Active is not null && installation.Ownership != RuntimeOwnership.Managed
            || state.ConfigurationIdentity is not null && !Digest(state.ConfigurationIdentity)
            || state.Active is not null && state.ConfigurationIdentity is null
            || state.Active is not null && !state.Revisions.Any(r => Same(r, state.Active))
            || state.Receipts.Any(r => r is null || r.OperationId == Guid.Empty || !Digest(r.RequestHash) || !Digest(r.IdempotencyHash)
                || !Enum.IsDefined(r.Outcome) || r.Outcome == RuntimeSwitchOutcome.RecoveryRequired
                || r.ConfigurationNetworkIntentIdentity is { } networkIntent && (!Digest(networkIntent)
                    || installation.RuntimeId != "mihomo" || installation.Target.Os != "linux" || r.ActiveRevisionId is null
                    || r.RemovedMihomoTun is not null))
            || state.Receipts.Select(r => r.OperationId).Distinct().Count() != state.Receipts.Length
            || state.Receipts.Select(r => r.IdempotencyHash).Distinct().Count() != state.Receipts.Length
            || !ValidTunStarts(state)
            || !ValidTunStops(state)
            || state.ActiveMihomoTun is { } tun && (installation.RuntimeId != "mihomo" || installation.Target.Os != "linux"
                || tun.OperationId == Guid.Empty || state.Active?.RevisionId != tun.RevisionId
                || state.ConfigurationIdentity != tun.ConfigurationIdentity || !Digest(tun.NetworkIntentIdentity)
                || state.Pending is not null || state.Configuration is not null
                || state.Removal is not null && state.PendingMihomoTunStart is not null
                || !state.Receipts.Any(r => r.OperationId == tun.OperationId && r.ActiveRevisionId == tun.RevisionId
                    && r.Outcome == RuntimeSwitchOutcome.Succeeded && r.ProblemCode is null))
            || state.ProxyPublication is { } publication && (installation.RuntimeId != "sing-box"
                || publication.OperationId == Guid.Empty || !Digest(publication.CandidateHash)
                || !Digest(publication.PreviousHash) || !Enum.IsDefined(publication.Outcome))
            || state.ProxyPublicationBaseline is { } baseline
                && (!state.ProxyPublicationUnresolved || baseline.Running && !baseline.Existed)
            || (state.Pending is null ? 0 : 1) + (state.Removal is null ? 0 : 1) + (state.Configuration is null ? 0 : 1) > 1)
            throw new RuntimePolicyException("runtime.transaction_state_invalid");
        if (state.Pending is { } pending)
        {
            if (pending.OperationId == Guid.Empty || !Digest(pending.RequestHash) || !Digest(pending.IdempotencyHash)
                || pending.Plan is null || pending.Plan.Request.InstallationId != installation.InstallationId
                || pending.Plan.Request.HostId != installation.HostId || pending.Plan.RuntimeId != installation.RuntimeId
                || pending.Stage is not (Protocol.Installations.InstallationStage.Preflight or Protocol.Installations.InstallationStage.Preparing
                    or Protocol.Installations.InstallationStage.Verifying or Protocol.Installations.InstallationStage.Activating
                    or Protocol.Installations.InstallationStage.HealthChecking or Protocol.Installations.InstallationStage.RollingBack
                    or Protocol.Installations.InstallationStage.Interrupted)
                || pending.Plan.Release is null || pending.Plan.Release.RuntimeId != installation.RuntimeId
                || pending.Plan.Release.Target != installation.Target || pending.Plan.Request.ReleaseId != pending.Plan.Release.ReleaseId
                || pending.Plan.PreviousReleaseId != pending.Previous?.Release.ReleaseId
                || pending.Plan.Request.ExpectedStateRevision < 0 || pending.Plan.Request.ExpectedStateRevision >= installation.StateRevision
                || pending.Plan.Request.ExpectedTrustRevision < 1 || pending.Plan.Request.ExpectedCatalogRevision < 1
                || pending.Plan.Request.Change is not (RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Repair or RuntimeChange.Rollback)
                || pending.Previous is not null && !state.Revisions.Any(r => Same(r, pending.Previous))
                || pending.Prepared is { } prepared && (prepared.Revision.RevisionId != pending.OperationId
                    || !state.Revisions.Any(r => Same(r, prepared.Revision)))
                || pending.SwitchStarted && (pending.Snapshot is null || pending.Prepared is null)
                || !pending.SwitchStarted && !Same(state.Active, pending.Previous)
                || pending.SwitchStarted && !Same(state.Active, pending.Previous) && !Same(state.Active, pending.Prepared!.Revision)
                || pending.Snapshot is { } snapshot && ((snapshot.ConfigurationIdentity is null) != (snapshot.ConfigurationSnapshotId is null)
                    || snapshot.ConfigurationIdentity is not null && !Digest(snapshot.ConfigurationIdentity))
                || !pending.SwitchStarted && pending.Snapshot is { } before && state.ConfigurationIdentity != before.ConfigurationIdentity
                || pending.SwitchStarted && Same(state.Active, pending.Previous) && state.ConfigurationIdentity != pending.Snapshot!.ConfigurationIdentity
                || pending.SwitchStarted && Same(state.Active, pending.Prepared!.Revision)
                    && state.ConfigurationIdentity != pending.Prepared.Revision.ConfigurationIdentity
                || state.Receipts.Any(r => r.OperationId == pending.OperationId || r.IdempotencyHash == pending.IdempotencyHash))
                throw new RuntimePolicyException("runtime.transaction_state_invalid");
        }
        else if (state.Removal is { } removal)
        {
            if (removal.OperationId == Guid.Empty || !Digest(removal.RequestHash) || !Digest(removal.IdempotencyHash)
                || removal.Plan is null || removal.Plan.Request.Change != Protocol.ManagedRuntimes.RuntimeChange.Uninstall
                || removal.Plan.Release is not null || removal.Plan.Request.ReleaseId is not null
                || removal.Plan.Request.InstallationId != installation.InstallationId
                || removal.Plan.Request.HostId != installation.HostId || removal.Plan.RuntimeId != installation.RuntimeId
                || removal.Plan.PreviousReleaseId != state.Active?.Release.ReleaseId
                || state.Active is null || installation.Ownership != RuntimeOwnership.Managed
                || removal.Plan.Request.ExpectedStateRevision < 0 || removal.Plan.Request.ExpectedStateRevision >= installation.StateRevision
                || removal.Plan.Request.ExpectedTrustRevision < 0 || removal.Plan.Request.ExpectedCatalogRevision < 0
                || removal.Stage is not (Protocol.Installations.InstallationStage.Preflight
                    or Protocol.Installations.InstallationStage.Activating or Protocol.Installations.InstallationStage.Verifying
                    or Protocol.Installations.InstallationStage.RollingBack or Protocol.Installations.InstallationStage.Interrupted)
                || removal.CleanupStarted && !removal.RemovalStarted
                || state.ActiveMihomoTun is not null && (state.HasMihomoTunAction(removal.OperationId)
                    || removal.RemovalStarted && removal.Snapshot is not { Running: false, BootEnabled: false })
                || removal.Snapshot is { } snapshot && ((snapshot.ConfigurationIdentity is null) != (snapshot.ConfigurationSnapshotId is null)
                    || snapshot.ConfigurationIdentity is not null && !Digest(snapshot.ConfigurationIdentity))
                || removal.Snapshot is not null && removal.Snapshot.ConfigurationIdentity != state.ConfigurationIdentity
                || state.Receipts.Any(r => r.OperationId == removal.OperationId || r.IdempotencyHash == removal.IdempotencyHash)
                || !removal.RemovalStarted && removal.Stage == Protocol.Installations.InstallationStage.Verifying)
                throw new RuntimePolicyException("runtime.transaction_state_invalid");
        }
        else if (state.Configuration is { } configuration)
        {
            var plan = configuration.Plan;
            if (configuration.OperationId == Guid.Empty || !Digest(configuration.RequestHash) || !Digest(configuration.IdempotencyHash)
                || plan is null || state.Active is null || installation.Ownership != RuntimeOwnership.Managed
                || plan.InstallationId != installation.InstallationId || plan.HostId != installation.HostId
                || plan.RuntimeId != installation.RuntimeId || plan.ActiveRevisionId != state.Active.RevisionId
                || plan.ExpectedStateRevision < 0 || plan.ExpectedStateRevision >= installation.StateRevision
                || !Digest(plan.PreviousIdentity) || !Digest(plan.CandidateIdentity) || plan.PreviousIdentity == plan.CandidateIdentity
                || plan.NetworkIntentIdentity is { } network && (plan.RuntimeId != "mihomo" || !Digest(network))
                || configuration.Stage is not (Protocol.Installations.InstallationStage.Preflight
                    or Protocol.Installations.InstallationStage.Preparing or Protocol.Installations.InstallationStage.Verifying
                    or Protocol.Installations.InstallationStage.Activating or Protocol.Installations.InstallationStage.HealthChecking
                    or Protocol.Installations.InstallationStage.RollingBack or Protocol.Installations.InstallationStage.Interrupted)
                || configuration.SwitchStarted && configuration.Snapshot is null
                || configuration.PreviousRestored && !configuration.SwitchStarted
                || configuration.RestoredNetworkCheckpoint.HasValue && (!configuration.PreviousRestored || plan.NetworkIntentIdentity is null)
                || configuration.RecoveryStopDispatched is true && !configuration.SwitchStarted
                || state.ConfigurationIdentity != (configuration.SwitchStarted && !configuration.PreviousRestored
                    ? plan.CandidateIdentity : plan.PreviousIdentity)
                || configuration.Snapshot is { } before && (before.ConfigurationIdentity != plan.PreviousIdentity
                    || before.ConfigurationSnapshotId != configuration.OperationId)
                || state.Receipts.Any(r => r.OperationId == configuration.OperationId || r.IdempotencyHash == configuration.IdempotencyHash))
                throw new RuntimePolicyException("runtime.transaction_state_invalid");
        }
        else if (installation.RecoveryRequired) throw new RuntimePolicyException("runtime.transaction_state_invalid");
    }

    private static bool ValidTunStarts(RuntimeArchiveState state)
    {
        var starts = state.MihomoTunStarts;
        return starts is { Length: <= 256 }
            && state.Receipts.Where(receipt => receipt.RemovedMihomoTun is not null).All(receipt =>
                state.Installation.RuntimeId == "mihomo" && receipt.Outcome == RuntimeSwitchOutcome.Succeeded
                && receipt.ActiveRevisionId is null && ValidRemovedTun(state, receipt.RemovedMihomoTun!))
            && starts.All(start => start is not null && start.OperationId != Guid.Empty && Enum.IsDefined(start.Stage)
                && (start.Restart || start.Stage != RuntimeMihomoTunStartStage.Stopping)
                && (start.PublicationId == state.ActiveMihomoTun?.OperationId
                    || start.Stage == RuntimeMihomoTunStartStage.Succeeded
                        && state.Receipts.Any(receipt => receipt.RemovedMihomoTun?.OperationId == start.PublicationId))
                && !state.Receipts.Any(receipt => receipt.OperationId == start.OperationId))
            && starts.Select(start => start.OperationId).Distinct().Count() == starts.Length
            && starts.Take(starts.Length - 1).All(start => start.Stage == RuntimeMihomoTunStartStage.Succeeded);
    }

    private static bool ValidRemovedTun(RuntimeArchiveState state, RuntimeMihomoTunOwnership owner) =>
        owner.OperationId != state.ActiveMihomoTun?.OperationId && owner.RevisionId != Guid.Empty
        && Digest(owner.ConfigurationIdentity) && Digest(owner.NetworkIntentIdentity)
        && state.Receipts.Any(receipt => receipt.OperationId == owner.OperationId && receipt.Outcome == RuntimeSwitchOutcome.Succeeded
            && receipt.ActiveRevisionId == owner.RevisionId);

    private static bool ValidTunStops(RuntimeArchiveState state)
    {
        var stops = state.MihomoTunStops;
        if (stops is not { Length: <= 256 }) return false;
        var ids = state.Receipts.Select(receipt => receipt.OperationId)
            .Concat(state.MihomoTunStarts.Select(start => start.OperationId)).ToHashSet();
        return stops.All(stop => stop is not null && stop.OperationId != Guid.Empty && ids.Add(stop.OperationId)
                && (stop.PublicationId == state.ActiveMihomoTun?.OperationId
                    || stop.Confirmed && state.Receipts.Any(receipt => receipt.RemovedMihomoTun?.OperationId == stop.PublicationId))
                && (stop.TargetExecutionId == stop.PublicationId || state.MihomoTunStarts.Any(start =>
                    start.OperationId == stop.TargetExecutionId && start.PublicationId == stop.PublicationId
                    && start.Stage == RuntimeMihomoTunStartStage.Succeeded)))
            && stops.Take(stops.Length - 1).All(stop => stop.Confirmed)
            && (state.PendingMihomoTunStop is not { } pending || pending.TargetExecutionId == state.CurrentMihomoTunExecution
                && state.PendingMihomoTunStart is null && state.Removal is null);
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Same(RuntimeStoredRevision? left, RuntimeStoredRevision? right) =>
        RuntimeJson.Serialize(left).AsSpan().SequenceEqual(RuntimeJson.Serialize(right));

    public sealed class Session : IDisposable
    {
        private readonly RuntimeArchiveStateStore store;
        private readonly FileStream gate;
        private bool disposed;
        internal Session(RuntimeArchiveStateStore store, FileStream gate, RuntimeArchiveState? state)
        { this.store = store; this.gate = gate; State = state; }
        public RuntimeArchiveState? State { get; private set; }
        internal void SyncDirectory()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (OperatingSystem.IsLinux()) store.SyncDirectory();
        }
        public void Save(RuntimeArchiveState state)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State is { } previous && (state.Installation.InstallationId != previous.Installation.InstallationId
                || state.Installation.HostId != previous.Installation.HostId || state.Installation.RuntimeId != previous.Installation.RuntimeId
                || state.Installation.Target != previous.Installation.Target
                || state.Installation.StateRevision != checked(previous.Installation.StateRevision + 1)))
                throw new RuntimePolicyException("runtime.state_revision_conflict");
            if (state.ActiveMihomoTun != State?.ActiveMihomoTun && !ValidTunOwnerTransition(State, state))
                throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
            if (State?.ProxyPublication is { Outcome: ProxyServerPublicationOutcome.InFlight } publication
                && (state.ProxyPublication != (publication with { Outcome = ProxyServerPublicationOutcome.Committed })
                    || state.ConfigurationIdentity != publication.CandidateHash))
                throw new RuntimePolicyException("runtime.publication_receipt_conflict");
            Validate(state);
            if (!ValidTunStartTransition(State?.MihomoTunStarts ?? [], state.MihomoTunStarts))
                throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
            if (!ValidTunStopTransition(State?.MihomoTunStops ?? [], state.MihomoTunStops))
                throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
            store.PersistCommittedWitness(State, state);
            store.WriteCore(state);
            State = state;
        }

        private static bool ValidTunOwnerTransition(RuntimeArchiveState? previous, RuntimeArchiveState next)
        {
            if (previous?.ActiveMihomoTun is { } removed)
                return next.ActiveMihomoTun is null && next.Removal is null && next.Active is null
                    && previous.Removal is { CleanupStarted: true } removal
                    && next.Receipts.Any(receipt => receipt.OperationId == removal.OperationId && receipt.RemovedMihomoTun == removed);
            return previous?.Configuration is { Stage: Protocol.Installations.InstallationStage.HealthChecking, SwitchStarted: true } original
                && original.Plan.NetworkIntentIdentity is { } network
                && next.ActiveMihomoTun == new RuntimeMihomoTunOwnership(original.OperationId,
                    original.Plan.ActiveRevisionId, original.Plan.CandidateIdentity, network);
        }

        private static bool ValidTunStartTransition(RuntimeMihomoTunStart[] previous, RuntimeMihomoTunStart[] next)
        {
            if (previous.SequenceEqual(next)) return true;
            if (previous.Length + 1 == next.Length && previous.SequenceEqual(next.Take(previous.Length)))
                return previous.LastOrDefault() is null or { Stage: RuntimeMihomoTunStartStage.Succeeded }
                    && next[^1].Stage == (next[^1].Restart ? RuntimeMihomoTunStartStage.Stopping : RuntimeMihomoTunStartStage.Launching);
            return previous.Length == next.Length && previous.Length != 0
                && previous.Take(previous.Length - 1).SequenceEqual(next.Take(next.Length - 1))
                && previous[^1].Stage != RuntimeMihomoTunStartStage.Succeeded
                && next[^1] == previous[^1] with { Stage = previous[^1].Stage + 1 };
        }

        public void RecordMihomoTunStart(RuntimeMihomoTunStart start)
        {
            var current = State ?? throw new RuntimePolicyException("runtime.state_unknown");
            var prior = current.MihomoTunStarts;
            RuntimeMihomoTunStart[] next = prior.LastOrDefault()?.OperationId == start.OperationId
                ? [.. prior.Take(prior.Length - 1), start] : [.. prior, start];
            Save(current with { MihomoTunStarts = next, Installation = current.Installation with
                { StateRevision = checked(current.Installation.StateRevision + 1) } });
            SyncDirectory();
        }

        private static bool ValidTunStopTransition(RuntimeMihomoTunStop[] previous, RuntimeMihomoTunStop[] next)
        {
            if (previous.SequenceEqual(next)) return true;
            if (previous.Length + 1 == next.Length && previous.SequenceEqual(next.Take(previous.Length)))
                return previous.LastOrDefault() is null or { Confirmed: true } && !next[^1].Confirmed;
            return previous.Length == next.Length && previous.Length > 0 && !previous[^1].Confirmed
                && previous.Take(previous.Length - 1).SequenceEqual(next.Take(next.Length - 1))
                && next[^1] == previous[^1] with { Confirmed = true };
        }

        public void RecordMihomoTunStop(RuntimeMihomoTunStop stop)
        {
            var current = State ?? throw new RuntimePolicyException("runtime.state_unknown");
            var prior = current.MihomoTunStops;
            RuntimeMihomoTunStop[] next = stop.Confirmed ? [.. prior.Take(prior.Length - 1), stop] : [.. prior, stop];
            Save(current with { MihomoTunStops = next, Installation = current.Installation with
                { StateRevision = checked(current.Installation.StateRevision + 1) } });
            SyncDirectory();
        }
        // Intent and failed-attempt evidence do not change the observed installation revision.
        // A successful native configuration commit still advances the revision through Save.
        public void RecordProxyPublication(ProxyServerPublicationReceipt receipt, ProxyPublicationServiceBaseline? baseline = null)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = State ?? throw new RuntimePolicyException("runtime.state_unknown");
            var previous = current.ProxyPublication;
            var valid = previous is { Outcome: ProxyServerPublicationOutcome.InFlight }
                ? receipt.Outcome == ProxyServerPublicationOutcome.Compensated && receipt == (previous with { Outcome = receipt.Outcome })
                : (receipt.Outcome is ProxyServerPublicationOutcome.InFlight or ProxyServerPublicationOutcome.Rejected)
                  && receipt.OperationId != previous?.OperationId;
            if (!valid || receipt.PreviousHash != current.ConfigurationIdentity
                || (receipt.Outcome == ProxyServerPublicationOutcome.InFlight) != (baseline is not null))
                throw new RuntimePolicyException("runtime.publication_receipt_conflict");
            var updated = current with { ProxyPublication = receipt, ProxyPublicationBaseline = baseline };
            store.WriteCore(updated);
            State = updated;
        }

        public void Dispose() { if (disposed) return; disposed = true; gate.Dispose(); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void SyncDirectory() => LinuxDirectorySync.Flush(directory);
}
