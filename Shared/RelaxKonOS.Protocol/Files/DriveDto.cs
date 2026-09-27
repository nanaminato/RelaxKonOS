using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>驱动器/根挂载点信息。Windows 为盘符（C:\ 等），Linux 为单条 "/"。<see cref="IsBrowsable"/>
/// is evaluated for the current authenticated session, rather than merely reporting that the host
/// filesystem exists.</summary>
public sealed record DriveDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("totalSize")] long? TotalSize,
    [property: JsonPropertyName("isReady")] bool IsReady,
    [property: JsonPropertyName("isBrowsable")] bool IsBrowsable);
