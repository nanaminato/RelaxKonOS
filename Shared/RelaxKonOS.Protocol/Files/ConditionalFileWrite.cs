using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>Null ExpectedVersion creates a new file exclusively; a SHA-256 replaces that version.</summary>
public sealed record ConditionalFileWriteRequest(
    [property: JsonPropertyName("path"), JsonRequired] string Path,
    [property: JsonPropertyName("content"), JsonRequired] byte[] Content,
    [property: JsonPropertyName("expectedVersion"), JsonRequired] string? ExpectedVersion);
public sealed record ConditionalFileWriteResult([property: JsonPropertyName("version"), JsonRequired] string Version);
