using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>重命名请求。在源条目所在目录内改名（不跨目录移动）。</summary>
public sealed record RenameRequest(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("newName")] string NewName);
