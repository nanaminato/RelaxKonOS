using System.Net;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>The Helper is the native owner of a managed TUN. Direct TUN keeps its existing marker owner.</summary>
internal sealed class MihomoManagedTunOperations(MihomoPublicationStore store, MihomoRuntimeService runtime,
    MihomoConfigurationPublicationService publication, IProxySettingsReader settings, IProxyProfileRepository profiles,
    IProxyNetworkSafetyPlatform platform, IProxyTunSafetyService direct)
{
    internal const string QueueStage = "managed_tun";

    internal async Task<(bool Managed, string? Problem)> TryExecuteAsync(Guid operationId, ProxyTunOperation action,
        Guid profileId, IPAddress? managementAddress, ProxyOperationStageReporter stage, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Published is null && state.Pending is null) return (false, null);
        if (await direct.CaptureRecoveryAsync(includeCurrentSession: true, ct) is not null)
            return (true, ProxyProblemCodes.RecoveryRequired);
        // Queue gate is acquired before the publication gate. The phase survives worker interruption.
        await stage(QueueStage);
        await store.Gate.WaitAsync(ct);
        var dispatched = false;
        try
        {
            state = await store.ReadAsync(ct);
            if (state.Pending is not null || state.DirectReload is not null || state.Published is not { } material)
                return (true, ProxyProblemCodes.RuntimeStateUnknown);
            if (!OperatingSystem.IsLinux()) return (true, ProxyProblemCodes.NotSupported);
            var observed = await runtime.QueryHelperAsync(ct, operationId);
            if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired
                || observed.Installation.Ownership != RuntimeOwnership.Managed || observed.ActiveRevisionId is not { } revision
                || observed.ConfigurationIdentity != state.Identity || observed.ProblemCode is not null)
                return (true, ProxyProblemCodes.RuntimeStateUnknown);
            if (action != ProxyTunOperation.Enable)
            {
                if (observed.MihomoTunPublicationId is { } owner)
                {
                    dispatched = true;
                    return (true, Problem(await runtime.StopManagedTunUnderGateAsync(owner, CancellationToken.None)));
                }
                return (true, material.NetworkTransitionRequired ? ProxyProblemCodes.RuntimeStateUnknown : null);
            }

            var profile = await profiles.GetActiveAsync(ct);
            if (profile is null || profile.Id != profileId || profile.EngineId != MihomoEngine.Id)
                return (true, ProxyProblemCodes.ConfigInvalid);
            if (observed.MihomoTunPublicationId is { } committed)
            {
                if (observed.MihomoTunRunning == true) return (true, null);
                if (observed.MihomoTunRunning != false) return (true, ProxyProblemCodes.RuntimeStateUnknown);
                if (!LinuxMihomoTunScope.Read().AllowManagedPublication) return (true, ProxyProblemCodes.NotSupported);
                dispatched = true;
                return (true, Problem(await runtime.StartManagedTunUnderGateAsync(operationId, committed, restart: false, CancellationToken.None)));
            }
            if (material.NetworkTransitionRequired) return (true, ProxyProblemCodes.RuntimeStateUnknown);
            var snapshot = await platform.CaptureManagementRouteAsync(managementAddress, ct);
            if (snapshot is null) return (true, ProxyProblemCodes.ManagementRouteUnsafe);
            var tun = (await settings.GetAsync(ct)).Tun ?? ProxyTunSettingsDto.Default;
            var plan = new RuntimeConfigurationPlan(observed.Installation.InstallationId, observed.Installation.HostId, "mihomo",
                observed.Installation.StateRevision, revision, state.Identity!, state.Identity!, operationId.ToString("N"));
            var frozen = await MihomoManagedTunPlan.FreezeAsync(operationId, plan, material, tun, snapshot, ct);
            dispatched = true;
            var result = await publication.PublishManagedTunUnderGateAsync(operationId, state.Revision, frozen, ct);
            return (true, result.Outcome switch
            {
                MihomoPublicationOutcome.Confirmed => null,
                MihomoPublicationOutcome.RecoveryRequired => ProxyProblemCodes.RecoveryRequired,
                _ => ProxyProblemCodes.ConfigApplyFailed
            });
        }
        catch (Exception error) when (error is RuntimeLifecycleException or IOException)
        { return (true, dispatched ? ProxyProblemCodes.RecoveryRequired : ProxyProblemCodes.RuntimeStateUnknown); }
        catch (RuntimePolicyException error)
        {
            return (true, error.ProblemCode switch
            {
                "runtime.network_lifecycle_unverified" or "runtime.network_scope_unavailable"
                    or "runtime.network_scope_changed" or "runtime.network_transaction_required" => ProxyProblemCodes.NotSupported,
                "runtime.management_route_unknown" or "runtime.management_route_changed" => ProxyProblemCodes.ManagementRouteUnsafe,
                _ => dispatched ? ProxyProblemCodes.RecoveryRequired : ProxyProblemCodes.RuntimeStateUnknown
            });
        }
        finally { store.Gate.Release(); }
    }

    private static string? Problem(RuntimeSwitchResult result) =>
        result.Outcome == RuntimeSwitchOutcome.Succeeded ? null : ProxyProblemCodes.RecoveryRequired;
}
