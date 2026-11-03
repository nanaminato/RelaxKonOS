using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Shared current-identity checks. Caller must hold the publication gate.</summary>
internal static class MihomoManagedPublicationVerification
{
    internal static async Task<RuntimeInstallationSnapshot> VerifyAsync(IProxyPlatformPaths paths,
        IProxyControllerSecretStore controllerSecrets, MihomoRuntimeService? managedRuntime,
        MihomoPublicationState state, CancellationToken ct)
    {
        if (managedRuntime is null || state.Published is null || state.Pending is not null)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        // ReadExisting never repairs or regenerates a revoked credential.
        if (await controllerSecrets.ReadExistingAsync(ct) != state.Published.ControllerSecret)
            throw new RuntimePolicyException("runtime.credentials_revoked");
        if (await new MihomoPublicationFiles(paths).IdentityAsync(ct) != state.Identity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        var observed = await managedRuntime.QueryHelperAsync(ct);
        if (observed.Installation.Ownership != RuntimeOwnership.Managed || observed.ActiveRevisionId is null
            || observed.PendingOperationId is not null || observed.Installation.RecoveryRequired || observed.ConfigurationIdentity != state.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        return observed;
    }

}
