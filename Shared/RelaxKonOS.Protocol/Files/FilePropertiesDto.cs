using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>文件或目录的详细属性。权限由宿主 OS 以当前 RelaxKonOS 服务进程身份读取。</summary>
public sealed record FilePropertiesDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] FileSystemEntryType Type,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("created")] DateTimeOffset? Created,
    [property: JsonPropertyName("modified")] DateTimeOffset? Modified,
    [property: JsonPropertyName("accessed")] DateTimeOffset? Accessed,
    [property: JsonPropertyName("permissions")] string Permissions,
    [property: JsonPropertyName("attributes")] string Attributes,
    /// <summary>Linux POSIX permission bits (for example 0755). Null on unsupported hosts.</summary>
    [property: JsonPropertyName("unixMode")] int? UnixMode);
