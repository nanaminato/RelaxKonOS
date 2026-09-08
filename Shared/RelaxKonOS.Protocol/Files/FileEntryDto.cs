using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>文件条目（含扩展名与非空大小）。对应 Jaya Shared/Models/FileModel。用于 <see cref="DirectoryDto.Files"/> 列表。</summary>
public sealed record FileEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("extension")] string? Extension,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("created")] DateTimeOffset? Created,
    [property: JsonPropertyName("modified")] DateTimeOffset? Modified,
    [property: JsonPropertyName("accessed")] DateTimeOffset? Accessed,
    [property: JsonPropertyName("isHidden")] bool IsHidden,
    [property: JsonPropertyName("isSystem")] bool IsSystem,
    [property: JsonPropertyName("mimeType")] string MimeType);
