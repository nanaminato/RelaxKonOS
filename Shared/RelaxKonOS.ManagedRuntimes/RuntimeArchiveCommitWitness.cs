using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed partial class RuntimeArchiveStateStore
{
    private string CommitsDirectory => Path.Combine(directory, "commits");
    private sealed record CommitRecord(RuntimeSwitchReceipt Receipt, RuntimeCommitWitness Witness);

    /// <summary>Read historical proof only when its exact receipt is committed in the supplied protected state.</summary>
    public RuntimeCommitWitness? ReadCommittedWitness(RuntimeArchiveState state, RuntimeSwitchReceipt? receipt)
    {
        if (receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded } || !state.Receipts.Contains(receipt)) return null;
        Prepare();
        RuntimeSourceStore.RequireNoLinks(CommitsDirectory);
        if (!Directory.Exists(CommitsDirectory)) return null;
        protectDirectory(CommitsDirectory);
        var path = CommitPath(receipt.OperationId);
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path)) return null;
        var saved = ReadCommit(path);
        // A proof staged before an interrupted state replacement is not a committed receipt.
        // The operation may subsequently have compensated instead; never treat that proof as success.
        if (saved.Receipt != receipt || saved.Witness.OperationId != receipt.OperationId
            || receipt.ActiveRevisionId != saved.Witness.ActiveRevisionId || receipt.ProblemCode is not null
            || !RuntimeCommitWitnessRules.Valid(saved.Witness)
            || saved.Witness.Installation.InstallationId != state.Installation.InstallationId
            || saved.Witness.Installation.HostId != state.Installation.HostId
            || saved.Witness.Installation.RuntimeId != state.Installation.RuntimeId
            || saved.Witness.Installation.Target != state.Installation.Target
            || saved.Witness.Installation.StateRevision > state.Installation.StateRevision)
            throw new RuntimePolicyException("runtime.commit_witness_invalid");
        return saved.Witness;
    }

    private void PersistCommittedWitness(RuntimeArchiveState? previous, RuntimeArchiveState state)
    {
        if (previous is null || state.Pending is not null || state.Removal is not null || state.Configuration is not null) return;
        var pendingId = previous.Pending?.OperationId ?? previous.Removal?.OperationId ?? previous.Configuration?.OperationId;
        if (pendingId is null) return;
        var receipt = state.Receipts.SingleOrDefault(item => item.OperationId == pendingId && item.Outcome == RuntimeSwitchOutcome.Succeeded);
        if (receipt is null || previous.Receipts.Any(item => item.OperationId == pendingId)) return;
        var verified = previous.Pending?.Stage == InstallationStage.HealthChecking
            || previous.Removal?.Stage == InstallationStage.Verifying || previous.Configuration?.Stage == InstallationStage.HealthChecking;
        if (!verified) throw new RuntimePolicyException("runtime.commit_witness_invalid");
        var witness = new RuntimeCommitWitness(receipt.OperationId, state.Installation, state.Active?.RevisionId,
            state.ConfigurationIdentity, state.Active?.MeasuredVersion);
        if (!RuntimeCommitWitnessRules.Valid(witness)) throw new RuntimePolicyException("runtime.commit_witness_invalid");
        RuntimeSourceStore.RequireNoLinks(CommitsDirectory);
        if (!Directory.Exists(CommitsDirectory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(CommitsDirectory);
            else Directory.CreateDirectory(CommitsDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protectDirectory(CommitsDirectory);
        var path = CommitPath(receipt.OperationId);
        RuntimeSourceStore.RequireNoLinks(path);
        var record = new CommitRecord(receipt, witness);
        var bytes = RuntimeJson.Serialize(record);
        if (File.Exists(path))
        {
            if (!RuntimeJson.Serialize(ReadCommit(path)).AsSpan().SequenceEqual(bytes))
                throw new RuntimePolicyException("runtime.commit_witness_conflict");
            return;
        }
        var temporary = Path.Combine(CommitsDirectory, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) { file.Write(bytes); file.Flush(true); }
            // Publish proof first. A crash leaves an ignored orphan until the exact success receipt commits.
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string CommitPath(Guid operationId) => Path.Combine(CommitsDirectory, operationId.ToString("D") + ".json");
    private static CommitRecord ReadCommit(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 32768) throw new RuntimePolicyException("runtime.commit_witness_invalid");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        var record = RuntimeJson.Read<CommitRecord>(bytes);
        if (record is not { Receipt: { }, Witness: { } }) throw new RuntimePolicyException("runtime.commit_witness_invalid");
        return record;
    }
}
