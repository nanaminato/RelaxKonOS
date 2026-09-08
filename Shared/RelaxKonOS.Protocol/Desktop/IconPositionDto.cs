using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Desktop;

/// <summary>桌面图标位置。X/Y 为桌面坐标（不依赖 Core.Primitives.Point，避免线协议与 Core 版本耦合）。</summary>
public sealed record IconPositionDto(
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);
