using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>特殊文件夹位置：Explorer 导航窗格快捷入口（主目录组节点下的叶子）。
/// 由 Server 端 <c>IFileService.GetSpecialLocations</c> 跨平台枚举（<c>Environment.GetFolderPath(SpecialFolder.*)</c>），
/// 仅返回 <c>Directory.Exists</c> 的项；缺失项已被服务端过滤。</summary>
public sealed record SpecialLocationDto(
    [property: JsonPropertyName("kind")] SpecialFolderKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path);
