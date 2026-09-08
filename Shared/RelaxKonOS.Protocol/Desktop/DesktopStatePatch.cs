using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Desktop;

/// <summary>桌面状态增量更新。仅非 null 字段表示变更。TaskbarState 暂用 string 占位，未来替换为结构化 DTO。</summary>
public sealed record DesktopStatePatch(
    [property: JsonPropertyName("wallpaper")] WallpaperDto? Wallpaper,
    [property: JsonPropertyName("theme")] ThemeKind? Theme,
    [property: JsonPropertyName("iconChanges")] IReadOnlyList<IconPositionDto>? IconChanges,
    [property: JsonPropertyName("taskbarState")] string? TaskbarState);
