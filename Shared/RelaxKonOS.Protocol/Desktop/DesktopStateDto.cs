using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Desktop;

/// <summary>桌面环境状态快照（壁纸 / 主题 / 图标布局 / 任务栏）。多设备通过 Workspace Hub 同步此状态。</summary>
public sealed record DesktopStateDto(
    [property: JsonPropertyName("wallpaper")] WallpaperDto Wallpaper,
    [property: JsonPropertyName("theme")] ThemeKind Theme,
    [property: JsonPropertyName("icons")] IReadOnlyList<IconPositionDto> Icons,
    [property: JsonPropertyName("taskbarState")] string TaskbarState);
