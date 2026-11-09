using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Guard execution under the existing publication authority.</summary>
public sealed class MihomoSystemProxyReconciler(
    IProxyPlatformPaths paths,
    IProxySettingsReader settingsReader,
    IProxyControllerSecretStore controllerSecrets,
    Platform.IHostSystemProxyService systemProxyService,
    MihomoPublicationStore publication,
    IMihomoRuntimeReceiptReader? managedRuntime = null,
    MihomoSettingsPublicationFiles? settingsFiles = null,
    Func<IMihomoControllerClient>? observeController = null) : IProxySystemProxyReconciler
{
    public async Task<string?> ReconcileSystemProxyAsync(bool startup, CancellationToken cancellationToken)
    {
        await publication.Gate.WaitAsync(cancellationToken);
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending is not null) return ProxyProblemCodes.RecoveryRequired;
            if (state.Published is not null)
            {
                var allowRetainedRemoval = state.LastRemoval is { Confirmed: true }
                    && !(await settingsReader.GetAsync(cancellationToken)).SystemProxyEnabled;
                await MihomoManagedPublicationVerification.VerifyAsync(paths, controllerSecrets, managedRuntime, state,
                    cancellationToken, allowRetainedRemoval);
            }
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
            if (state.DirectReload is { RequiresSavedListener: true } direct)
            {
                var intent = direct.ConfirmedSettings!;
                if (settings != intent.Previous) return ProxyProblemCodes.RecoveryRequired;
                var observed = observeController is null ? null : await observeController().GetListenerStateAsync(cancellationToken);
                var readyPort = observed is { Succeeded: true, Value: { } listener } ? listener.MixedPort : 0;
                if (!await IsListenerReadyAsync(settings, readyPort, cancellationToken)
                    && settingsFiles is not null
                    && (settings with { MixedPort = intent.Candidate.MixedPort, LogLevel = intent.Candidate.LogLevel }) == intent.Candidate
                    && await IsListenerReadyAsync(intent.Candidate, readyPort, cancellationToken)
                    && await settingsFiles.ObserveTrackedEffectAsync(direct.OperationId, intent, cancellationToken) is { Applied: true } effect)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (effect.Current)
                    {
                        if (unresolvedHost) await publication.DeferTrackedHostGuardUnderGateAsync(state);
                        return ProxyProblemCodes.RecoveryRequired;
                    }
                    if (guarded)
                    {
                        // Preserve the saved host policy while the acknowledged candidate port is live.
                        if (unresolvedHost) await publication.SupersedeTrackedHostPolicyUnderGateAsync(state);
                        var problem = await systemProxyService.ApplyAsync(settings with { MixedPort = readyPort }, settings,
                            enforce: true, CancellationToken.None);
                        return problem ?? ProxyProblemCodes.RecoveryRequired;
                    }
                }
            }
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

    private static async Task<bool> IsListenerReadyAsync(ProxySettingsDto settings, int observedPort, CancellationToken ct) =>
        observedPort == settings.MixedPort && await MihomoListenerProbe.ObserveAsync(settings, ct, requireSocks: true) == true;
}
