using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>One protected per-instance state file is also the atomic active pointer. All mutations share its lock.</summary>
public sealed class RuntimeArchiveStateStore(string directory, Action<string> protectDirectory)
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
        return ReadCore();
    }

    public Session Acquire()
    {
        Prepare();
        var path = Path.Combine(directory, "instance.lock");
        RuntimeSourceStore.RequireNoLinks(path);
        FileStream gate;
        try { gate = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new RuntimePolicyException("runtime.instance_busy"); }
        try { return new(this, gate, ReadCore()); }
        catch { gate.Dispose(); throw; }
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
                || !Enum.IsDefined(r.Outcome) || r.Outcome == RuntimeSwitchOutcome.RecoveryRequired)
            || state.Receipts.Select(r => r.OperationId).Distinct().Count() != state.Receipts.Length
            || state.Receipts.Select(r => r.IdempotencyHash).Distinct().Count() != state.Receipts.Length)
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
        else if (installation.RecoveryRequired) throw new RuntimePolicyException("runtime.transaction_state_invalid");
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
        public void Save(RuntimeArchiveState state)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State is { } previous && (state.Installation.InstallationId != previous.Installation.InstallationId
                || state.Installation.HostId != previous.Installation.HostId || state.Installation.RuntimeId != previous.Installation.RuntimeId
                || state.Installation.Target != previous.Installation.Target
                || state.Installation.StateRevision != checked(previous.Installation.StateRevision + 1)))
                throw new RuntimePolicyException("runtime.state_revision_conflict");
            store.WriteCore(state);
            State = state;
        }
        public void Dispose() { if (disposed) return; disposed = true; gate.Dispose(); }
    }
}
