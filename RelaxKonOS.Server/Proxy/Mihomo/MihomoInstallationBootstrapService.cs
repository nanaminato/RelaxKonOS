using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Server-owned material selection for the installation ledger. It does not read profile drafts,
/// stage unverified resources, start a service, or activate host proxy/TUN state.</summary>
public sealed class MihomoInstallationBootstrapService(MihomoPublicationStore store, MihomoInstallationPublicationService installation,
    IProxySettingsService settings, IProxyControllerSecretStore secrets, MihomoControllerOptions options)
{
    public async Task<MihomoPublicationResult> InstallAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        _ = MihomoRuntimeMaterial.InstallationPlanIdentity(plan);
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is { } pending)
            {
                if (pending.OperationId != operationId || pending.InstallationPlan is null
                    || !RuntimeJson.Serialize(pending.InstallationPlan).AsSpan().SequenceEqual(RuntimeJson.Serialize(plan)))
                    throw new RuntimePolicyException("runtime.publication_unconfirmed");
                // Replay selection uses the encrypted candidate. Never recreate secrets or read current drafts.
                return await installation.InstallUnderGateAsync(operationId, state.Revision, plan, pending.Candidate, ct);
            }
            var material = state.Published;
            if (material is null)
            {
                if (state.LastInstallation?.OperationId == operationId)
                    throw new RuntimePolicyException("runtime.operation_already_completed");
                var current = await settings.GetAsync(ct);
                string secret;
                try { secret = await secrets.GetOrCreateAsync(ct); }
                catch (ProxyControllerSecretException) { throw new RuntimePolicyException("runtime.credentials_revoked"); }
                material = CreateMaterial(current, options, secret);
            }
            return await installation.InstallUnderGateAsync(operationId, state.Revision, plan, material, ct);
        }
        finally { store.Gate.Release(); }
    }

    internal static MihomoPublishedRuntime CreateMaterial(ProxySettingsDto current, MihomoControllerOptions options, string secret)
    {
        // A first installation has no published profile. Use an explicit DIRECT baseline with the current
        // host-owned listener/settings, disabled TUN and no GEO/provider resource references or downloads.
        // DNS has no server listen address here; profile publication owns any explicit DNS listener later.
        var yaml = MihomoManagedConfiguration.WithServerControllerSettings(
            MihomoManagedConfiguration.WithRuntimeSettings(
                MihomoManagedConfiguration.WithManagedTunSettings(
                    MihomoManagedConfiguration.WithServerGeoDataSettings("mode: rule\nrules:\n  - MATCH,DIRECT\n"), current, false, []), current), options, secret);
        var description = MihomoConfigurationPolicy.Describe(yaml);
        if (description.ResourcePaths.Count != 0 || description.Material.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.configuration_unsupported");
        return description.Material;
    }
}
