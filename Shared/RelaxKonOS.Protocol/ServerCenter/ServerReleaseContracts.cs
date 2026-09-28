using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 发布包内单个文件的摘要条目，用于检查包内文件完整性。
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
/// 官方稳定版目录项。它描述某个 RID 的当前稳定包及其 SHA-256。
/// </summary>
public sealed record ServerReleaseDescriptorDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageKind")] ServerReleasePackageKind PackageKind,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] ServerRuntimeIdentifier Runtime,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256);
