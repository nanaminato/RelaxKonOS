using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>Updates Linux POSIX permission bits for an entry and optionally its descendants (symbolic links are skipped).</summary>
public sealed record UpdateUnixPermissionsRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("unixMode")] int UnixMode,
    [property: JsonPropertyName("recursive")] bool Recursive);
