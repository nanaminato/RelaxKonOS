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
        var clock = time ?? TimeProvider.System;
        var state = await store.ReadAsync(ct);
        if (state.Pending is not null || state.Published is not { } published)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        // The Server-owned current file is a drift check, never an alternate publication authority.
        var path = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        RuntimeSourceStore.RequireNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != state.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        string? secret;
        try { secret = await secrets.ReadExistingAsync(ct); }
        catch (ProxyControllerSecretException) { throw new RuntimePolicyException("runtime.credentials_revoked"); }
        if (secret is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(published.ControllerSecret)))
            throw new RuntimePolicyException("runtime.credentials_revoked");
        return new(clock.GetUtcNow().AddSeconds(30), published);
    }
}
