using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Guard execution under the existing publication authority; no settings writer or controller client.</summary>
public sealed class MihomoSystemProxyReconciler(
    IProxyPlatformPaths paths,
    IProxySettingsReader settingsReader,
    IProxyControllerSecretStore controllerSecrets,
    Platform.IHostSystemProxyService systemProxyService,
    MihomoPublicationStore publication,
    MihomoRuntimeService? managedRuntime = null,
    MihomoSettingsPublicationFiles? settingsFiles = null) : IProxySystemProxyReconciler
{
    public async Task<string?> ReconcileSystemProxyAsync(bool startup, CancellationToken cancellationToken)
    {
        await publication.Gate.WaitAsync(cancellationToken);
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending is not null) return ProxyProblemCodes.RecoveryRequired;
            if (state.Published is not null) await MihomoManagedPublicationVerification.VerifyAsync(paths, controllerSecrets, managedRuntime, state, cancellationToken);
            var unresolvedHost = state.DirectReload is { TrackedHostPolicy: true, HostGuardSuperseded: false };
            if (unresolvedHost && settingsFiles is not null)
            {
                try
                {
                    var result = await publication.RecoverDirectReloadUnderGateAsync(state, null, cancellationToken, settingsFiles);
                    if (result.Outcome == RuntimeSwitchOutcome.Succeeded) return null;
                }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.settings_drift")
                {
                    // The normal guard still owns the persisted policy, even if the transaction cannot commit it.
                }
            }
            // Direct reload recovery retains the prior settings. Guard still owns that host intent.
            var settings = await settingsReader.GetAsync(cancellationToken);
            if (state.Published is not null && settingsFiles is not null)
                await settingsFiles.CaptureAsync(settings, settings, cancellationToken);
            var guarded = settings.SystemProxyEnabled && settings.SystemProxy?.GuardEnabled == true;
            if (!startup && !guarded) return null;
            cancellationToken.ThrowIfCancellationRequested();
            // From this point a distinct safety write can alter the observed host policy.
            // Its effects must never be used later to settle the original transaction.
            if (unresolvedHost) await publication.SupersedeTrackedHostPolicyUnderGateAsync(state);
            // The Helper can commit the environment before delivering its response. Keep the
            // publication gate through the real reply once the operation has been dispatched.
            return await systemProxyService.ApplyAsync(settings, settings, enforce: guarded, CancellationToken.None);
        }
        catch (Exception ex) when (ex is RuntimePolicyException or RuntimeLifecycleException or IOException or InvalidDataException or ProxyControllerSecretException)
        { return ProxyProblemCodes.RecoveryRequired; }
        finally { publication.Gate.Release(); }
    }

}
