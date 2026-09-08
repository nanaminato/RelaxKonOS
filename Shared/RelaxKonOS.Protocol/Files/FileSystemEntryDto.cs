using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>文件系统条目（目录/驱动器/通用 info）。对应 Jaya Shared/Models/FileSystemObjectModel 与 DirectoryModel。
/// 文件条目（含扩展名）用 <see cref="FileEntryDto"/>。</summary>
public sealed record FileSystemEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("type")] FileSystemEntryType Type,
    [property: JsonPropertyName("created")] DateTimeOffset? Created,
    [property: JsonPropertyName("modified")] DateTimeOffset? Modified,
    [property: JsonPropertyName("accessed")] DateTimeOffset? Accessed,
    [property: JsonPropertyName("isHidden")] bool IsHidden,
    [property: JsonPropertyName("isSystem")] bool IsSystem,
    [property: JsonPropertyName("mimeType")] string? MimeType);
