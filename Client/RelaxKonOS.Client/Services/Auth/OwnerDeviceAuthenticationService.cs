using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Coordinates key storage with the owner-device challenge protocol.</summary>
public sealed class OwnerDeviceAuthenticationService(IRelaxKonOSClient client, IOwnerDeviceKeyStore keys)
{
    public async Task<LoginResponse> BootstrapWindowsAsync(ServerConnectionIdentity identity, string deviceName, string clientVersion,
        CancellationToken ct = default)
    {
        var material = await keys.CreateAsync(identity.ServiceId, deviceName, "windows", clientVersion, passphrase: null, ct);
        try
        {
            var response = await client.BootstrapWindowsOwnerDeviceAsync(identity.EffectiveBaseUrl,
                new OwnerDeviceBootstrapRequest(material.DeviceName, material.Platform, material.PublicKeySpki, material.ClientVersion), ct);
            await keys.SaveAsync(material with { DeviceId = response.Device.Id }, passphrase: null, ct);
            return response;
        }
        catch
        {
            await keys.RemoveAsync(identity.ServiceId, passphrase: null, ct);
            throw;
        }
    }

    public async Task<LoginResponse> SignInAsync(ServerConnectionIdentity identity, string? passphrase, CancellationToken ct = default)
    {
        var material = await keys.LoadAsync(identity.ServiceId, passphrase, ct)
            ?? throw new OwnerDeviceNotPairedException();
        if (material.DeviceId == Guid.Empty) throw new InvalidOperationException("The saved owner-device key is not enrolled.");
        var challenge = await client.CreateOwnerDeviceChallengeAsync(identity.EffectiveBaseUrl,
            new OwnerDeviceChallengeRequest(material.DeviceId), ct);
        var nonce = DecodeBase64Url(challenge.Nonce);
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(material.PrivateKeyPkcs8, out _);
            var signature = Convert.ToBase64String(key.SignData(nonce, HashAlgorithmName.SHA256));
            return await client.SignInWithOwnerDeviceAsync(identity.EffectiveBaseUrl,
                new OwnerDeviceSignInRequest(challenge.ChallengeId, material.DeviceId, signature), ct);
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }
    }

    public async Task<string> CreatePairingPayloadAsync(ServerConnectionIdentity identity, string publicPairingUrl,
        string accessToken, CancellationToken ct = default)
    {
        var publicEndpoint = OwnerDevicePairingEndpointRules.Normalize(publicPairingUrl);
        var invitation = await client.CreateOwnerDeviceInvitationAsync(identity.EffectiveBaseUrl, accessToken, ct);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new OwnerDevicePairingPayload(1, publicEndpoint, invitation.Token, invitation.ExpiresAt), RelaxKonOSJsonOptions.Default)));
    }

    public async Task<LoginResponse> AcceptPairingPayloadAsync(string payload, string deviceName, string platform, string clientVersion,
        string? passphrase, CancellationToken ct = default)
    {
        var pairing = ParsePairingPayload(payload);
        var identity = ServerConnectionIdentityRules.Direct(pairing.ServerUrl);
        var material = await keys.CreateAsync(identity.ServiceId, deviceName, platform, clientVersion, passphrase, ct);
        try
        {
            var enrolled = await client.AcceptOwnerDeviceInvitationAsync(identity.EffectiveBaseUrl,
                new OwnerDeviceAcceptInvitationRequest(pairing.Token, material.DeviceName, material.Platform, material.PublicKeySpki,
                    material.ClientVersion), ct);
            material = material with { DeviceId = enrolled.Id };
            await keys.SaveAsync(material, passphrase, ct);
            return await SignInAsync(identity, passphrase, ct);
        }
        catch
        {
            await keys.RemoveAsync(identity.ServiceId, passphrase, ct);
            throw;
        }
    }

    public OwnerDeviceKeyStorageKind GetStorageKind(string serviceId) => keys.GetStorageKind(serviceId);

    public static OwnerDevicePairingPayload ParsePairingPayload(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OwnerDevicePairingPayload>(Convert.FromBase64String(payload.Trim()), RelaxKonOSJsonOptions.Default);
            if (parsed is null || parsed.Version != 1 || string.IsNullOrWhiteSpace(parsed.Token)
                || parsed.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new FormatException();
            return parsed with { ServerUrl = OwnerDevicePairingEndpointRules.Normalize(parsed.ServerUrl) };
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException("The pairing code is invalid or has expired.", exception);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// UTF-8 payload placed in a QR code or copied as a pairing code. It carries an invitation only,
/// never a private key or an access token.
/// </summary>
public sealed record OwnerDevicePairingPayload(int Version, string ServerUrl, string Token, DateTimeOffset ExpiresAt);

/// <summary>Raised when this client has no private key enrolled with the selected Server.</summary>
public sealed class OwnerDeviceNotPairedException : InvalidOperationException
{
    public OwnerDeviceNotPairedException() : base("This device has not been paired with the selected Server.") { }
}
