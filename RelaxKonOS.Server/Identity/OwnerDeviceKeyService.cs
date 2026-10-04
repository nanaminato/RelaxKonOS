using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Issues and verifies proof-of-possession challenges for owner devices. This service is disabled
/// outside Windows 10/11 workstation editions, including every Windows Server SKU.
/// </summary>
/// <remarks>
/// A challenge signature is ECDSA/SHA-256 over the raw 32-byte nonce, encoded as the RFC 3279 /
/// X9.62 ASN.1 DER SEQUENCE. That is the only form every client platform emits natively: Android
/// Keystore signs through <c>SHA256withECDSA</c>, and any JVM or OpenSSL caller produces the same
/// bytes. .NET's <c>ECDsa.SignData</c>/<c>VerifyData</c> instead default to the fixed-field IEEE
/// P1363 <c>r‖s</c> concatenation, which is not this contract; the format must therefore always be
/// passed explicitly. Omitting it silently rejects every Android and OpenSSL signature.
/// </remarks>
public sealed class OwnerDeviceKeyService(IServiceScopeFactory scopes)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, PendingChallenge> _challenges = new();
    private readonly ConcurrentDictionary<string, PendingInvitation> _invitations = new(StringComparer.Ordinal);

    public bool IsAvailable => WindowsWorkstationPlatform.IsWindows10Or11Workstation();

    public OwnerDeviceChallenge CreateChallenge(Guid deviceId)
    {
        RequireAvailable();
        if (WithKeys(keys => keys.FindActive(deviceId)) is null) throw new OwnerDeviceKeyException(404, "owner-device-not-found");
        Prune();
        var id = Guid.NewGuid();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var expiresAt = DateTimeOffset.UtcNow.Add(ChallengeLifetime);
        if (!_challenges.TryAdd(id, new(deviceId, nonce, expiresAt))) throw new InvalidOperationException("Could not create owner-device challenge.");
        return new(id, Base64Url(nonce), expiresAt);
    }

    public OwnerDeviceKey VerifyChallenge(Guid challengeId, Guid deviceId, string signature)
    {
        RequireAvailable();
        if (!_challenges.TryRemove(challengeId, out var challenge)
            || challenge.DeviceId != deviceId || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new OwnerDeviceKeyException(401, "owner-device-challenge-invalid");
        return WithKeys(keys =>
        {
            var key = keys.FindActive(deviceId) ?? throw new OwnerDeviceKeyException(401, "owner-device-revoked");
            try
            {
                var signatureBytes = Convert.FromBase64String(signature);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKeySpki), out _);
                if (!ecdsa.VerifyData(challenge.Nonce, signatureBytes, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence))
                    throw new OwnerDeviceKeyException(401, "owner-device-signature-invalid");
            }
            catch (FormatException) { throw new OwnerDeviceKeyException(400, "owner-device-signature-invalid"); }
            catch (CryptographicException) { throw new OwnerDeviceKeyException(401, "owner-device-signature-invalid"); }
            key.LastUsedAt = DateTimeOffset.UtcNow;
            keys.Update(key);
            return key;
        });
    }

    public OwnerDeviceKey Register(Guid userId, Guid deviceId, OwnerDeviceBootstrapRequest request)
    {
        RequireAvailable();
        ValidateRequest(request.DeviceName, request.Platform, request.ClientVersion, request.PublicKeySpki);
        return WithKeys(keys =>
        {
            if (keys.FindActive(deviceId) is not null) throw new OwnerDeviceKeyException(409, "owner-device-already-enrolled");
            return keys.Add(new OwnerDeviceKey
            {
                Id = deviceId,
                UserId = userId,
                DeviceId = deviceId,
                Name = request.DeviceName.Trim(),
                Platform = request.Platform.Trim().ToLowerInvariant(),
                ClientVersion = request.ClientVersion.Trim(),
                PublicKeySpki = request.PublicKeySpki,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        });
    }

    /// <summary>
    /// Registers the local Windows device, or replaces its lost local key. The caller is already
    /// constrained by the loopback Negotiate route and authenticated Windows administrator policy.
    /// Desktop processes restrict enrollment to their own account; built-in System Mode service
    /// identities permit administrators to enroll their own canonical user account.
    /// It is intentionally not available to ordinary owner-device or remote sessions.
    /// </summary>
    public OwnerDeviceKey RegisterOrReplaceLocalWindowsDevice(Guid userId, Guid deviceId, OwnerDeviceBootstrapRequest request)
    {
        RequireAvailable();
        ValidateRequest(request.DeviceName, request.Platform, request.ClientVersion, request.PublicKeySpki);
        return WithKeys(keys =>
        {
            var existing = keys.FindActive(deviceId);
            if (existing is null)
            {
                return keys.Add(new OwnerDeviceKey
                {
                    Id = deviceId,
                    UserId = userId,
                    DeviceId = deviceId,
                    Name = request.DeviceName.Trim(),
                    Platform = request.Platform.Trim().ToLowerInvariant(),
                    ClientVersion = request.ClientVersion.Trim(),
                    PublicKeySpki = request.PublicKeySpki,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            if (existing.UserId != userId)
                throw new OwnerDeviceKeyException(409, "owner-device-already-enrolled");

            existing.Name = request.DeviceName.Trim();
            existing.Platform = request.Platform.Trim().ToLowerInvariant();
            existing.ClientVersion = request.ClientVersion.Trim();
            existing.PublicKeySpki = request.PublicKeySpki;
            existing.LastUsedAt = null;
            keys.Update(existing);
            return existing;
        });
    }

    public OwnerDeviceInvitation CreateInvitation(ClaimsPrincipal principal)
    {
        var owner = RequireOwner(principal);
        Prune();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        _invitations[token] = new(owner.UserId, expiresAt);
        return new(token, expiresAt);
    }

    public OwnerDeviceKey AcceptInvitation(OwnerDeviceAcceptInvitationRequest request, Guid deviceId)
    {
        RequireAvailable();
        if (string.IsNullOrWhiteSpace(request.Token) || !_invitations.TryRemove(request.Token, out var invitation)
            || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new OwnerDeviceKeyException(401, "owner-device-invitation-invalid");
        var bootstrap = new OwnerDeviceBootstrapRequest(request.DeviceName, request.Platform, request.PublicKeySpki, request.ClientVersion);
        return Register(invitation.UserId, deviceId, bootstrap);
    }

    /// <summary>Checks an invitation before the endpoint allocates a Device row.</summary>
    public void EnsureInvitationIsUsable(string token)
    {
        RequireAvailable();
        if (string.IsNullOrWhiteSpace(token) || !_invitations.TryGetValue(token, out var invitation)
            || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new OwnerDeviceKeyException(401, "owner-device-invitation-invalid");
    }

    public bool IsOwner(ClaimsPrincipal principal)
    {
        // A device id alone is not proof of possession: normal password/session logins use the
        // same device record. Only the short-lived JWT minted after this key signed a nonce may
        // bypass an administrator-password prompt, and it still has to be the controller session.
        if (!IsAvailable
            || !string.Equals(principal.FindFirst("amr")?.Value, "owner-device-key", StringComparison.Ordinal)
            || !string.Equals(principal.FindFirst("role")?.Value, "controller", StringComparison.Ordinal)
            || !TryIdentity(principal, out var userId, out var deviceId)) return false;
        var key = WithKeys(keys => keys.FindActive(deviceId));
        return key is { UserId: var owner } && owner == userId;
    }

    public OwnerDeviceKey RequireOwner(ClaimsPrincipal principal)
    {
        if (!IsOwner(principal)) throw new OwnerDeviceKeyException(403, "owner-device-required");
        return WithKeys(keys => keys.FindActive(Guid.Parse(principal.FindFirst("device_id")!.Value))!);
    }

    public IReadOnlyList<OwnerDeviceKey> List(ClaimsPrincipal principal)
    {
        RequireOwner(principal);
        return WithKeys(keys => (IReadOnlyList<OwnerDeviceKey>)keys.ListActive(Guid.Parse(principal.FindFirst("sub")!.Value)).ToArray());
    }

    public void Revoke(ClaimsPrincipal principal, Guid deviceId)
    {
        var actor = RequireOwner(principal);
        WithKeys(keys =>
        {
            var key = keys.FindActive(deviceId) ?? throw new OwnerDeviceKeyException(404, "owner-device-not-found");
            if (key.UserId != actor.UserId) throw new OwnerDeviceKeyException(404, "owner-device-not-found");
            if (key.Id == actor.Id && keys.ListActive(actor.UserId).Count == 1)
                throw new OwnerDeviceKeyException(409, "owner-device-last-device");
            key.RevokedAt = DateTimeOffset.UtcNow;
            keys.Update(key);
            return 0;
        });
    }

    private void RequireAvailable()
    {
        if (!IsAvailable) throw new OwnerDeviceKeyException(404, "owner-device-unsupported-platform");
    }

    private static void ValidateRequest(string name, string platform, string version, string publicKeySpki)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || string.IsNullOrWhiteSpace(platform) || platform.Length > 32
            || string.IsNullOrWhiteSpace(version) || version.Length > 64 || string.IsNullOrWhiteSpace(publicKeySpki) || publicKeySpki.Length > 512)
            throw new OwnerDeviceKeyException(400, "owner-device-invalid-request");
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            if (ecdsa.KeySize != 256) throw new OwnerDeviceKeyException(400, "owner-device-key-algorithm-invalid");
        }
        catch (FormatException) { throw new OwnerDeviceKeyException(400, "owner-device-key-invalid"); }
        catch (CryptographicException) { throw new OwnerDeviceKeyException(400, "owner-device-key-invalid"); }
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _challenges.Where(pair => pair.Value.ExpiresAt <= now)) _challenges.TryRemove(pair.Key, out _);
        foreach (var pair in _invitations.Where(pair => pair.Value.ExpiresAt <= now)) _invitations.TryRemove(pair.Key, out _);
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out Guid userId, out Guid deviceId)
    {
        userId = Guid.Empty;
        deviceId = Guid.Empty;
        return Guid.TryParse(principal.FindFirst("sub")?.Value, out userId)
            && Guid.TryParse(principal.FindFirst("device_id")?.Value, out deviceId);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private T WithKeys<T>(Func<IOwnerDeviceKeyRepository, T> action)
    {
        using var scope = scopes.CreateScope();
        return action(scope.ServiceProvider.GetRequiredService<IOwnerDeviceKeyRepository>());
    }

    private sealed record PendingChallenge(Guid DeviceId, byte[] Nonce, DateTimeOffset ExpiresAt);
    private sealed record PendingInvitation(Guid UserId, DateTimeOffset ExpiresAt);
}

public sealed class OwnerDeviceKeyException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
