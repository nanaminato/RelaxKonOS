using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Persisted dimensions for every application and modal window in a workspace.</summary>
public sealed record WorkspaceWindowLayoutDto(
    [property: JsonPropertyName("windows")] IReadOnlyList<WindowSizeDto> Windows)
{
    private WorkspaceWindowLayoutDto() : this(new List<WindowSizeDto>()) { }

    public static WorkspaceWindowLayoutDto Default { get; } = new(Array.Empty<WindowSizeDto>());
}

/// <summary>One stable window-layout key and its content size in desktop pixels.</summary>
public sealed record WindowSizeDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);
