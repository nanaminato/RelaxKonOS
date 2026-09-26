using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 发布包内单个文件的摘要条目。签名覆盖清单，清单覆盖每个文件摘要，因此签名 + 摘要链能证明
/// 包内容既来自可信发布者，也没有在传输中被替换。
/// </summary>
public sealed record ServerReleaseFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// 包内 <c>manifest.json</c>。它列出运行时标识、支持的系统与逐文件摘要；安装器据此做
/// 架构、系统与完整性校验，客户端据此做目标 RID 与包大小核对。
/// </summary>
public sealed record ServerReleaseManifestDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageKind")] ServerReleasePackageKind PackageKind,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] ServerRuntimeIdentifier Runtime,
    [property: JsonPropertyName("supportedSystems")] IReadOnlyList<string> SupportedSystems,
    [property: JsonPropertyName("payload")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Payload,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset? CreatedAtUtc,
    [property: JsonPropertyName("files")] IReadOnlyList<ServerReleaseFileDto> Files);

/// <summary>
/// 离线发布密钥对某段字节（清单规范序列化字节或下载描述符）的签名。
/// <paramref name="ManifestSha256"/> 绑定被签名对象的摘要，避免签名被挪用到另一份内容。
/// </summary>
/// <param name="KeyId">客户端内置可信公钥的标识；用于密钥轮换与撤销。</param>
/// <param name="Algorithm">签名算法，例如 <c>ed25519</c> 或 <c>rsa-pss-sha256</c>。</param>
public sealed record ServerReleaseSignatureDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("signedSha256")] string SignedSha256,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>
/// 官方稳定版目录项。它描述某个 RID 的当前稳定包及其 SHA-256；HTTPS 与 SHA-256 只用于传输完整性，
/// 不足以单独证明发布者身份，因此目录项本身必须由 <c>*.sig</c> 伴随文件签名。
/// </summary>
public sealed record ServerReleaseDescriptorDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageKind")] ServerReleasePackageKind PackageKind,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] ServerRuntimeIdentifier Runtime,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// 发布信任策略。正式发布必须验证离线发布签名；只有显式开发配置才允许自签来源，
/// 且该例外必须在客户端可见地标注，不能作为默认路径。
/// </summary>
/// <param name="TrustedKeyIds">客户端内置的可信发布公钥标识。</param>
public sealed record ServerReleaseTrustPolicy(
    [property: JsonPropertyName("trustedKeyIds")] IReadOnlyList<string> TrustedKeyIds,
    [property: JsonPropertyName("allowDevelopmentSource")] bool AllowDevelopmentSource = false)
{
    /// <summary>默认策略：不内置任何密钥时不得信任任何来源，也不允许开发来源。</summary>
    public static ServerReleaseTrustPolicy Strict { get; } = new([], AllowDevelopmentSource: false);

    public bool IsTrustedKey(string keyId) =>
        TrustedKeyIds.Contains(keyId, StringComparer.Ordinal);

    /// <summary>开发来源仅在显式开启开发配置时被接受。</summary>
    public bool AcceptsUntrustedSource => AllowDevelopmentSource;
}

/// <summary>发布校验的机器可读结果，供客户端与启动器展示“包来源已确认”的证据链。</summary>
public sealed record ServerReleaseVerificationDto(
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("packageKind")] ServerReleasePackageKind? PackageKind,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("runtime")] ServerRuntimeIdentifier? Runtime,
    [property: JsonPropertyName("keyId")] string? KeyId,
    [property: JsonPropertyName("problemCode")] string? ProblemCode,
    [property: JsonPropertyName("verifiedAtUtc")] DateTimeOffset VerifiedAtUtc);
