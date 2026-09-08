using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>移动请求。可跨目录。目标存在且 overwrite=false 时返回 409 already-exists。</summary>
public sealed record MoveRequest(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("destinationPath")] string DestinationPath,
    [property: JsonPropertyName("overwrite")] bool Overwrite = false);
