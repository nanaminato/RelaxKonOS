using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Desktop;

/// <summary>壁纸描述。Key 为壁纸资源标识，Url 为可选的资源地址。</summary>
public sealed record WallpaperDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("url")] string? Url);
