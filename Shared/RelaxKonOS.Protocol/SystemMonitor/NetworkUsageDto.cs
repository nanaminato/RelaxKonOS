using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>单个网络接口累计字节计数与瞬时速率（字节/秒，由服务端相邻采样差分计算）。</summary>
public sealed record NetworkUsageDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bytesSent")] long BytesSent,
    [property: JsonPropertyName("bytesReceived")] long BytesReceived,
    [property: JsonPropertyName("sendRateBytesPerSec")] long SendRateBytesPerSec,
    [property: JsonPropertyName("receiveRateBytesPerSec")] long ReceiveRateBytesPerSec);
