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
            else if (await publication.UncoordinatedWriteProblemAsync(cancellationToken) is { } publicationProblem) return publicationProblem;
            var settings = await settingsReader.GetAsync(cancellationToken);
            if (state.Published is not null && settingsFiles is not null)
                await settingsFiles.CaptureAsync(settings, settings, cancellationToken);
            var guarded = settings.SystemProxyEnabled && settings.SystemProxy?.GuardEnabled == true;
            if (!startup && !guarded) return null;
            return await systemProxyService.ApplyAsync(settings, settings, enforce: guarded, cancellationToken);
        }
        catch (Exception ex) when (ex is RuntimePolicyException or RuntimeLifecycleException or IOException or InvalidDataException or ProxyControllerSecretException)
        { return ProxyProblemCodes.RecoveryRequired; }
        finally { publication.Gate.Release(); }
    }

}
