using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ProxyServer;

public static class ProxyServerRoutes
{
    public const string Root = "/api/v1.0/proxy-server";
    public const string Status = Root + "/status";
    public const string Configuration = Root + "/configuration";
    public const string Apply = Configuration + "/apply";
    public const string Validate = Configuration + "/validate";
    public const string Runtime = Root + "/runtime/actions";
    public const string Credentials = Root + "/credentials";
    public const string Export = Root + "/export";
}

[JsonConverter(typeof(JsonStringEnumConverter<ProxyServerAction>))]
public enum ProxyServerAction { Install, Start, Stop, Restart, Enable, Disable, Recover }
[JsonConverter(typeof(JsonStringEnumConverter<ProxyServerProtocol>))]
public enum ProxyServerProtocol { VlessReality, VlessTls, VlessWebSocket, Trojan, Hysteria2 }

public sealed record ProxyServerInbound(Guid Id, string Name, ProxyServerProtocol Protocol,
    string ListenAddress, int ListenPort, string PublicHost, int PublicPort,
    string ServerName, string HandshakeHost, int HandshakePort = 443, bool Enabled = true,
    Guid? CertificateId = null, string WebSocketPath = "/proxy");
public sealed record ProxyServerCredential(Guid Id, string Name, Guid InboundId, bool Enabled,
    DateTimeOffset? ExpiresAt, bool Applied);
public sealed record ProxyServerConfiguration(long Revision, IReadOnlyList<ProxyServerInbound> Inbounds);
public sealed record SaveProxyServerConfiguration(long ExpectedRevision, IReadOnlyList<ProxyServerInbound> Inbounds);
public sealed record ProxyServerCredentialRequest(Guid? Id, string Name, Guid InboundId, bool Enabled = true,
    DateTimeOffset? ExpiresAt = null, bool Rotate = false);
public sealed record ProxyServerExportRequest(Guid CredentialId, string Format);
public sealed record ProxyServerExport(string Format, string Content);
public sealed record ProxyServerActionRequest(ProxyServerAction Action);
public sealed record ProxyServerValidation(bool Valid, IReadOnlyList<string> Problems);
public sealed record ProxyServerStatus(bool Supported, bool Installed, bool Running, bool RecoveryRequired,
    string? Version, string? ConfigurationHash, bool Applied, string? ProblemCode, bool StatisticsSupported = false);
public sealed record ProxyServerSnapshot(ProxyServerConfiguration Configuration,
    IReadOnlyList<ProxyServerCredential> Credentials, ProxyServerStatus Status, long? PublishedRevision);

// Helper-only structured material. Never returned by ordinary management reads.
public sealed record ProxyServerMaterial(ProxyServerConfiguration Configuration,
    IReadOnlyList<ProxyServerSecret> Credentials, IReadOnlyList<ProxyServerRealityKey> Keys,
    IReadOnlyList<ProxyServerTlsMaterial>? Certificates = null);
public sealed record ProxyServerTlsMaterial(Guid InboundId, string CertificatePem, string PrivateKeyPem, string Version);
public sealed record ProxyServerSecret(Guid Id, Guid InboundId, string Value);
public sealed record ProxyServerRealityKey(Guid InboundId, string PrivateKey, string PublicKey, string ShortId);
public sealed record ProxyServerHelperRequest(ProxyServerAction? Action = null, ProxyServerMaterial? Material = null);
