using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>Updates Linux POSIX permission bits for one file-system entry.</summary>
public sealed record UpdateUnixPermissionsRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("unixMode")] int UnixMode);
