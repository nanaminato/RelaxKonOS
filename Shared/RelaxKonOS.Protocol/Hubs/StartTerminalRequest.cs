using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>
/// 启动远端终端会话的请求。<see cref="Shell"/> 为 null 时由服务端按宿主 OS 选取默认 shell
/// （Windows: powershell→cmd 兜底；Linux: bash→sh 兜底）。
/// </summary>
public sealed record StartTerminalRequest(
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("rows")] int Rows,
    [property: JsonPropertyName("widthPixels")] int WidthPixels,
    [property: JsonPropertyName("heightPixels")] int HeightPixels,
    [property: JsonPropertyName("shell")] string? Shell,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory);
