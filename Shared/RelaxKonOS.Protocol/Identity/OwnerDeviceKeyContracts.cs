using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>
/// Windows 10/11 workstation owner-device authentication. A private P-256 key stays on the
/// enrolled client; the server stores only its SPKI public key.
/// </summary>
public static class OwnerDeviceKeyApiRoutes
{
    private const string Root = $"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/auth/owner-devices";

    public const string Bootstrap = Root + "/bootstrap";
    public const string LocalBootstrap = Root + "/local-bootstrap";
    public const string Challenge = Root + "/challenge";
    public const string SignIn = Root + "/sign-in";
    public const string Invitations = Root + "/invitations";
    public const string AcceptInvitation = Root + "/accept-invitation";
    public const string Devices = Root;
    public const string DeviceTemplate = Root + "/{id}";
    public static string Device(Guid id) => Root + "/" + id.ToString("D");
}

public sealed record OwnerDeviceBootstrapRequest(
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("publicKeySpki")] string PublicKeySpki,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);

public sealed record OwnerDeviceChallengeRequest(
    [property: JsonPropertyName("deviceId")] Guid DeviceId);

/// <summary>Base64url of the raw 32-byte nonce. Clients sign the decoded bytes, never this text.</summary>
public sealed record OwnerDeviceChallenge(
    [property: JsonPropertyName("challengeId")] Guid ChallengeId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>
/// Proves possession of an enrolled owner-device key. <see cref="Signature"/> is base64 of an
/// ECDSA/SHA-256 signature over the decoded nonce, encoded as the ASN.1 DER SEQUENCE of RFC 3279 /
/// X9.62 — the form Android Keystore, JVMs and OpenSSL emit natively. It is not the fixed-field
/// IEEE P1363 <c>r‖s</c> concatenation that .NET signs and verifies by default.
/// </summary>
public sealed record OwnerDeviceSignInRequest(
    [property: JsonPropertyName("challengeId")] Guid ChallengeId,
    [property: JsonPropertyName("deviceId")] Guid DeviceId,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record OwnerDeviceInvitationRequest(
    [property: JsonPropertyName("expiresInSeconds")] int? ExpiresInSeconds = null);

public sealed record OwnerDeviceInvitation(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>
/// Completes an owner-device enrollment. <see cref="DeviceName"/> is a display label;
/// server-issued device identities, rather than names, determine uniqueness.
/// </summary>
public sealed record OwnerDeviceAcceptInvitationRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("publicKeySpki")] string PublicKeySpki,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);

public sealed record OwnerDeviceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);
