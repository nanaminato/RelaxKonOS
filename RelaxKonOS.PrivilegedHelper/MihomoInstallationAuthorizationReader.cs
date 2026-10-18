using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Selected only for the durable Install transaction. Pending candidate authority never becomes
/// a normal current-publication grant, and retained-data reinstall cannot smuggle a configuration change.</summary>
internal sealed class MihomoInstallationAuthorizationReader(RuntimeArchiveStateStore states, RuntimeChangePlan plan,
    Func<CancellationToken, Task<MihomoInstallationAuthorization>> read) : IMihomoArchiveAuthorization
{
    public async Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct)
    {
        var state = states.Read() ?? throw new RuntimePolicyException("runtime.authorization_unavailable");
        if (state.Pending is not { Previous: null } pending || pending.Plan.Request.Change != RuntimeChange.Install
            || state.Active is not null && state.Active.RevisionId != pending.OperationId
            || !RuntimeJson.Serialize(plan).AsSpan().SequenceEqual(RuntimeJson.Serialize(pending.Plan)))
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        var grant = await read(ct);
        if (grant.OperationId != pending.OperationId || grant.PlanIdentity != MihomoRuntimeMaterial.InstallationPlanIdentity(pending.Plan))
            throw new RuntimePolicyException("runtime.authorization_unavailable");
        var retainedIdentity = pending.SwitchStarted ? pending.Snapshot?.ConfigurationIdentity : state.ConfigurationIdentity;
        if (retainedIdentity is not null && grant.Candidate is { } material
            && MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(material.Yaml)) != retainedIdentity)
            throw new RuntimePolicyException("runtime.authorization_changed");
        return new(grant.ValidUntil, grant.Candidate);
    }
}
