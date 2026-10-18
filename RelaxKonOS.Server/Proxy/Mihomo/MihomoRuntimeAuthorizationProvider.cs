using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Fresh confirmed publication only. The lifecycle caller owns Gate; this callback never reacquires it.</summary>
public sealed class MihomoRuntimeAuthorizationProvider(MihomoPublicationStore store,
    IProxyPlatformPaths paths, IProxyControllerSecretStore secrets, TimeProvider? time = null)
{
    public async Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Pending is not null)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        return await ReadConfirmedAsync(state, ct);
    }

    internal async Task<MihomoCurrentAuthorization> ReadRemovalAsync(Guid operationId, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Pending is not { RemovalPlan: not null } pending || pending.OperationId != operationId)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        return await ReadConfirmedAsync(state, ct);
    }

    private async Task<MihomoCurrentAuthorization> ReadConfirmedAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var clock = time ?? TimeProvider.System;
        if (state.Published is not { } published) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        // The Server-owned current file is a drift check, never an alternate publication authority.
        var path = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != state.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var secret = await ReadSecretAsync(ct);
        if (!Matches(secret, published))
            throw new RuntimePolicyException("runtime.credentials_revoked");
        return new(clock.GetUtcNow().AddSeconds(30), published);
    }

    /// <summary>Caller owns Gate. Each callback re-reads the named encrypted transaction and current secret;
    /// candidate and previous authorization are independent, and this never authorizes a binary switch.</summary>
    public async Task<MihomoConfigurationAuthorization> ReadConfigurationAsync(Guid operationId, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Pending is not { ConfigurationPlan: not null } pending || pending.OperationId != operationId
            || state.Published is not { } previous) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var identity = await new MihomoPublicationFiles(paths).IdentityAsync(ct);
        // Helper may have committed before the Server file/authority commit. Both exact frozen identities
        // are recoverable while this named publication remains pending; unrelated drafts remain forbidden.
        if (identity != state.Identity && identity != pending.Identity) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var secret = await ReadSecretAsync(ct);
        return new(operationId, (time ?? TimeProvider.System).GetUtcNow().AddSeconds(30),
            Matches(secret, previous) ? previous : null, Matches(secret, pending.Candidate) ? pending.Candidate : null);
    }

    private async Task<string?> ReadSecretAsync(CancellationToken ct)
    {
        try { return await secrets.ReadExistingAsync(ct); }
        catch (ProxyControllerSecretException) { throw new RuntimePolicyException("runtime.credentials_revoked"); }
    }

    public async Task<MihomoInstallationAuthorization> ReadInstallationAsync(Guid operationId, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Pending is not { InstallationPlan: not null, ConfigurationPlan: null } pending || pending.OperationId != operationId)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var mirror = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        RuntimeSourceStore.RequireNoLinks(mirror);
        if (state.Published is not null || File.Exists(mirror))
        {
            var identity = await new MihomoPublicationFiles(paths).IdentityAsync(ct);
            if (identity != (state.Identity ?? pending.Identity)) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        }
        var secret = await ReadSecretAsync(ct);
        return new(operationId, (time ?? TimeProvider.System).GetUtcNow().AddSeconds(30),
            MihomoRuntimeMaterial.InstallationPlanIdentity(pending.InstallationPlan), Matches(secret, pending.Candidate) ? pending.Candidate : null);
    }
    private static bool Matches(string? secret, MihomoPublishedRuntime material) => secret is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(material.ControllerSecret));
}
