namespace RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Wire contract for the built-in Proxy Server application (`relaxkonos.proxy-server`).
/// The management routes are host-global; the public subscription route is the only anonymous
/// surface and carries a single bearer token in its path.
/// </summary>
public static class ProxyServerRoutes
{
    public const string Root = "/api/v1.0/proxy-server";
    public const string Capabilities = Root + "/capabilities";
    public const string Status = Root + "/status";

    public const string RuntimeReleases = Root + "/runtime/releases";
    public const string RuntimeInstall = Root + "/runtime/install";
    public const string RuntimeUpgrade = Root + "/runtime/upgrade";
    public const string RuntimeUninstall = Root + "/runtime/uninstall";
    public const string RuntimeActions = Root + "/runtime/actions";
    public const string RuntimeActionPattern = RuntimeActions + "/{action}";

    public const string Configuration = Root + "/configuration";
    public const string Validate = Configuration + "/validate";
    public const string Apply = Configuration + "/apply";
    public const string Rollback = Configuration + "/rollback";
    public const string Recovery = Root + "/recovery";

    public const string Principals = Root + "/principals";
    public const string PrincipalPattern = Principals + "/{id:guid}";

    public const string Credentials = Root + "/credentials";
    public const string CredentialPattern = Credentials + "/{id:guid}";
    public const string CredentialRotatePattern = CredentialPattern + "/rotate";
    public const string CredentialExportPattern = CredentialPattern + "/export";
    public const string CredentialExportFormatsPattern = CredentialPattern + "/export-formats";

    public const string Subscriptions = Root + "/subscriptions";
    public const string SubscriptionPattern = Subscriptions + "/{id:guid}";

    public const string OperationPattern = Root + "/operations/{id:guid}";

    /// <summary>Anonymous subscription download. The token is the only credential and never a management login.</summary>
    public const string PublicSubscriptionRoot = "/api/v1.0/proxy-subscriptions";
    public const string PublicSubscriptionPattern = PublicSubscriptionRoot + "/{token}/{format}";
}

/// <summary>Closed set of proxy-server presets. Vision never combines with WebSocket.</summary>
public enum ProxyServerProtocol { VlessReality, VlessTls, VlessWebSocket, Trojan, Hysteria2 }

/// <summary>Closed set of runtime verbs. Business mutations are queued by the Server, never executed here.</summary>
public enum ProxyServerAction { Install, Upgrade, Uninstall, Start, Stop, Restart, Enable, Disable, Rollback, Recover }

/// <summary>Which secret shape a credential must carry. Never converted between protocols.</summary>
public enum ProxyServerCredentialKind { Uuid, Password }

public static class ProxyServerPresets
{
    public static bool IsVless(ProxyServerProtocol protocol) =>
        protocol is ProxyServerProtocol.VlessReality or ProxyServerProtocol.VlessTls or ProxyServerProtocol.VlessWebSocket;

    /// <summary>Hysteria2 listens on UDP; every other preset listens on TCP.</summary>
    public static bool IsUdp(ProxyServerProtocol protocol) => protocol == ProxyServerProtocol.Hysteria2;

    public static bool RequiresCertificate(ProxyServerProtocol protocol) => protocol != ProxyServerProtocol.VlessReality;

    public static ProxyServerCredentialKind CredentialKind(ProxyServerProtocol protocol) =>
        IsVless(protocol) ? ProxyServerCredentialKind.Uuid : ProxyServerCredentialKind.Password;

    /// <summary>A credential may span inbounds only when every inbound expects the same secret shape.</summary>
    public static bool Shareable(IReadOnlyList<ProxyServerProtocol> protocols) =>
        protocols.Count > 0 && protocols.Select(CredentialKind).Distinct().Count() == 1;
}

public static class ProxyServerExportFormats
{
    public const string Uri = "uri";
    public const string SingBox = "sing-box";
    public const string Mihomo = "mihomo";

    public static IReadOnlyList<string> All { get; } = [Uri, SingBox, Mihomo];
    public static bool IsKnown(string? format) => format is Uri or SingBox or Mihomo;
}

public sealed record ProxyServerInbound(
    Guid Id,
    string Name,
    ProxyServerProtocol Protocol,
    string ListenAddress,
    int ListenPort,
    string PublicHost,
    int PublicPort,
    string ServerName,
    Guid? CertificateId = null,
    string HandshakeHost = "",
    int HandshakePort = 443,
    string WebSocketPath = "/proxy",
    bool Enabled = true);

/// <summary>Desired configuration (the saved draft). Publication to the runtime is a separate revision.</summary>
public sealed record ProxyServerConfiguration(
    long Revision,
    IReadOnlyList<ProxyServerInbound> Inbounds,
    string? SubscriptionBaseUrl = null);

public sealed record SaveProxyServerConfiguration(
    long ExpectedRevision,
    IReadOnlyList<ProxyServerInbound> Inbounds,
    string? SubscriptionBaseUrl = null);

public sealed record ProxyServerPrincipal(
    Guid Id,
    string DisplayName,
    string? RelaxKonOSUserId,
    int CredentialCount);

public sealed record SaveProxyServerPrincipal(Guid? Id, string DisplayName, string? RelaxKonOSUserId);

/// <summary>Metadata only. The UUID or password stays behind the credential endpoint.</summary>
public sealed record ProxyServerCredential(
    Guid Id,
    Guid PrincipalId,
    string Name,
    IReadOnlyList<Guid> InboundIds,
    bool Enabled,
    DateTimeOffset? ExpiresAt,
    bool Applied);

public sealed record ProxyServerCredentialRequest(
    Guid? Id,
    Guid PrincipalId,
    string Name,
    IReadOnlyList<Guid> InboundIds,
    bool Enabled = true,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Rotation never returns the new secret; a lost secret is rotated again.</summary>
public sealed record ProxyServerCredentialRotation(bool? Enabled = null, DateTimeOffset? ExpiresAt = null, bool ClearExpiry = false);

public sealed record ProxyServerSubscription(
    Guid Id,
    Guid CredentialId,
    IReadOnlyList<string> Formats,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool Revoked);

/// <summary>The raw token appears exactly once, in the issuing response.</summary>
public sealed record IssueProxyServerSubscription(
    Guid CredentialId,
    IReadOnlyList<string> Formats,
    DateTimeOffset? ExpiresAt = null);

/// <summary>A replay returns the original subscription with an empty token and URL list; secrets are disclosed once.</summary>
public sealed record ProxyServerSubscriptionIssued(
    ProxyServerSubscription Subscription,
    string Token,
    IReadOnlyList<string> Urls);

public sealed record ProxyServerExportFormatOption(string Format, bool Supported, string? Reason);

public sealed record ProxyServerExportRequest(Guid CredentialId, string Format);

public sealed record ProxyServerExport(string Format, string Content, string? Warning = null);

public sealed record ProxyServerValidation(bool Valid, IReadOnlyList<string> Problems, string? Preview = null);

public sealed record ProxyServerProtocolCapability(
    ProxyServerProtocol Protocol,
    ProxyServerCredentialKind CredentialKind,
    bool Udp,
    bool RequiresCertificate,
    bool Verified);

/// <summary>What this host/build can do. Statistics and logs stay false until a trusted build proves them.</summary>
public sealed record ProxyServerCapabilities(
    bool Supported,
    string Platform,
    string Architecture,
    string KernelVersion,
    IReadOnlyList<ProxyServerProtocolCapability> Protocols,
    bool Statistics,
    bool Logs,
    bool ServiceControl,
    bool BootAutostart,
    bool Uninstall,
    bool SubscriptionHosting);

public sealed record ProxyServerStatus(
    bool Supported,
    bool Installed,
    bool Running,
    bool RecoveryRequired,
    string? Version,
    string? ConfigurationHash,
    bool Applied,
    string? ProblemCode,
    bool StatisticsSupported = false,
    bool? BootEnabled = null);

public sealed record ProxyServerRuntimeRelease(
    string Version,
    string Platform,
    string Architecture,
    string Sha256,
    bool Installed,
    IReadOnlyList<ProxyServerProtocol> VerifiedProtocols);

public sealed record ProxyServerUpgradeRequest(string Version);

public sealed record ProxyServerUninstallRequest(bool KeepData = true);

public sealed record ProxyServerSnapshot(
    ProxyServerConfiguration Configuration,
    IReadOnlyList<ProxyServerPrincipal> Principals,
    IReadOnlyList<ProxyServerCredential> Credentials,
    IReadOnlyList<ProxyServerSubscription> Subscriptions,
    ProxyServerStatus Status,
    ProxyServerCapabilities Capabilities,
    long? PublishedRevision,
    long? PreviousRevision);

// Helper-only structured material. Never returned by an ordinary management read.
public sealed record ProxyServerMaterial(
    ProxyServerConfiguration Configuration,
    IReadOnlyList<ProxyServerSecret> Credentials,
    IReadOnlyList<ProxyServerRealityKey> Keys,
    IReadOnlyList<ProxyServerTlsMaterial>? Certificates = null);

public sealed record ProxyServerTlsMaterial(Guid InboundId, string CertificatePem, string PrivateKeyPem, string Version);

/// <summary>One secret and every inbound it is authorized for. Inbounds must share the secret shape.</summary>
public sealed record ProxyServerSecret(Guid Id, IReadOnlyList<Guid> InboundIds, string Value);

public sealed record ProxyServerRealityKey(Guid InboundId, string PrivateKey, string PublicKey, string ShortId);

public sealed record ProxyServerHelperRequest(
    ProxyServerAction? Action = null,
    ProxyServerMaterial? Material = null,
    bool KeepData = true);
