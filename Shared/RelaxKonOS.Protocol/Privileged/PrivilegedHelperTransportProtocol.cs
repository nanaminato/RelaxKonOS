using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Transport-level contract for the persistent Helper that listens on a local Unix Domain Socket.
/// It is deliberately independent from <see cref="PrivilegedOperationProtocol"/>: the request and
/// result payloads keep their own version, while this contract only frames and negotiates them.
/// The Windows named-pipe host keeps its existing signed envelope; this contract is not used there.
/// </summary>
public static class PrivilegedHelperTransportProtocol
{
    /// <summary>Framing and handshake version. It changes only with this transport, never with a product release.</summary>
    public const string Version = "1.0";

    /// <summary>systemd creates this runtime directory; the helper only adjusts traversal for the Server group.</summary>
    public const string DefaultUnixSocketPath = "/run/relaxkonos/privileged.sock";

    /// <summary>A frame is a 4-byte little-endian length followed by that many UTF-8 JSON bytes.</summary>
    public const int MaximumFrameBytes = PrivilegedOperationProtocol.MaximumRequestBytes;

    /// <summary>The handshake is tiny; never let a peer allocate a request-sized buffer for it.</summary>
    public const int MaximumHandshakeFrameBytes = 4096;

    public const int DefaultMaximumConcurrentRequests = 8;
    public const int MaximumAllowedConcurrentRequests = 32;

    public const string ProgressCapability = "progress";
    public const string PeerCredentialCapability = "peer-credentials";
    public const string ReplayProtectionCapability = "replay-protection";

    /// <summary>Capabilities this transport version defines. Unknown values are rejected, not ignored.</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
        [ProgressCapability, PeerCredentialCapability, ReplayProtectionCapability];

    /// <summary>The Server needs progress frames; a helper that cannot stream them is incompatible.</summary>
    public static IReadOnlyList<string> RequiredCapabilities { get; } = [ProgressCapability];

    public static bool IsKnownCapability(string capability) => Capabilities.Contains(capability, StringComparer.Ordinal);
}

/// <summary>
/// First frame on every socket connection. The Server sends <see cref="HelloType"/>; the Helper
/// answers <see cref="WelcomeType"/> or <see cref="RejectedType"/> before any request is accepted.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PrivilegedHelperHandshake(string Type, string Version,
    IReadOnlyList<string>? Capabilities = null, PrivilegedProblemCode? ProblemCode = null, string? Error = null)
{
    public const string HelloType = "hello";
    public const string WelcomeType = "welcome";
    public const string RejectedType = "rejected";

    public static PrivilegedHelperHandshake Hello() =>
        new(HelloType, PrivilegedHelperTransportProtocol.Version, PrivilegedHelperTransportProtocol.RequiredCapabilities);

    public static PrivilegedHelperHandshake Welcome() =>
        new(WelcomeType, PrivilegedHelperTransportProtocol.Version, PrivilegedHelperTransportProtocol.Capabilities);

    public static PrivilegedHelperHandshake Rejected(PrivilegedProblemCode problemCode, string error) =>
        new(RejectedType, PrivilegedHelperTransportProtocol.Version, ProblemCode: problemCode, Error: error);

    /// <summary>
    /// Version mismatch is reported as <see cref="PrivilegedProblemCode.InvalidProtocol"/> so callers can
    /// tell "helper installed but incompatible" from "helper unavailable". Capabilities must be known and
    /// the welcome must grant everything the Hello required.
    /// </summary>
    public bool IsValid(PrivilegedHelperHandshake? requiredFrom = null)
    {
        if (string.IsNullOrWhiteSpace(Version)) return false;
        if (Capabilities is not null && Capabilities.Any(capability => !PrivilegedHelperTransportProtocol.IsKnownCapability(capability)))
            return false;
        return Type switch
        {
            HelloType => Version == PrivilegedHelperTransportProtocol.Version && Capabilities is not null
                && ProblemCode is null && Error is null,
            WelcomeType => Version == PrivilegedHelperTransportProtocol.Version && Capabilities is not null
                && ProblemCode is null && Error is null
                && (requiredFrom?.Capabilities ?? PrivilegedHelperTransportProtocol.RequiredCapabilities)
                    .All(capability => Capabilities.Contains(capability, StringComparer.Ordinal)),
            RejectedType => Version == PrivilegedHelperTransportProtocol.Version && Error is { Length: > 0 and <= 256 }
                && ProblemCode is { } code && Enum.IsDefined(code),
            _ => false
        };
    }
}
