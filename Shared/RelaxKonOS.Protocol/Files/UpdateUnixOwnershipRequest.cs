using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

public sealed record UpdateUnixOwnershipRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("recursive")] bool Recursive);
