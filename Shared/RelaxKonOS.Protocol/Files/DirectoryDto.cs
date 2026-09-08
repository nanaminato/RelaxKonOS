using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>目录列举结果。对应 Jaya Shared/Models/DirectoryModel：目录自身元数据 + 子目录列表 + 文件列表。</summary>
public sealed record DirectoryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] FileSystemEntryType Type,
    [property: JsonPropertyName("directories")] IReadOnlyList<FileSystemEntryDto> Directories,
    [property: JsonPropertyName("files")] IReadOnlyList<FileEntryDto> Files,
    [property: JsonPropertyName("created")] DateTimeOffset? Created,
    [property: JsonPropertyName("modified")] DateTimeOffset? Modified);
